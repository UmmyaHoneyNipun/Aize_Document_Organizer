using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.Channels;
using Aize.DocumentService.Application;
using Aize.DocumentService.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aize.DocumentService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDocumentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LocalBlobStorageOptions>(configuration.GetSection(LocalBlobStorageOptions.SectionName));
        services.Configure<PythonProcessorOptions>(configuration.GetSection(PythonProcessorOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDocumentRepository, InMemoryDocumentRepository>();
        services.AddSingleton<IDocumentReadModelRepository, InMemoryDocumentReadModelRepository>();
        services.AddSingleton<IDocumentProcessingQueue, InMemoryDocumentProcessingQueue>();
        services.AddSingleton<IDocumentMessagePublisher, InMemoryDocumentMessagePublisher>();
        services.AddSingleton<IObjectStorage, FileSystemObjectStorage>();
        services.AddSingleton<IDocumentNotificationPublisher, SignalRDocumentNotificationPublisher>();
        services.AddHttpClient<IPythonProcessorClient, PythonProcessorClient>();
        services.AddHostedService<QueuedDocumentDispatchBackgroundService>();
        services.AddSingleton<DocumentApplicationService>();

        return services;
    }
}

public sealed class LocalBlobStorageOptions
{
    public const string SectionName = "LocalBlobStorage";

    public string RootPath { get; set; } = "App_Data/blob-storage";
}

public sealed class PythonProcessorOptions
{
    public const string SectionName = "PythonProcessor";

    public string BaseUrl { get; set; } = "http://localhost:8001";
}

public interface IDocumentStatusClient
{
    Task DocumentUpdated(DocumentRealtimeMessage message);
}

public sealed class DocumentStatusHub : Hub<IDocumentStatusClient>
{
    public const string Route = "/hubs/documents";

    public static string GroupForUser(string userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupForUser(userId));
        }

        await base.OnConnectedAsync();
    }
}

internal sealed class FileSystemObjectStorage : IObjectStorage
{
    private readonly string _rootPath;

    public FileSystemObjectStorage(
        IOptions<LocalBlobStorageOptions> options,
        IHostEnvironment hostEnvironment)
    {
        _rootPath = Path.GetFullPath(Path.Combine(hostEnvironment.ContentRootPath, options.Value.RootPath));
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<ObjectStorageSaveResult> SaveAsync(
        Stream content,
        ObjectStorageSaveRequest request,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(request.OriginalFileName);
        var partition = DateTime.UtcNow.ToString("yyyy/MM/dd");
        var directory = Path.Combine(_rootPath, partition);
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, $"{request.DocumentId:N}{extension}");

        await using var fileStream = File.Create(filePath);
        await content.CopyToAsync(fileStream, cancellationToken);

        return new ObjectStorageSaveResult(filePath, fileStream.Length);
    }
}

internal sealed class InMemoryDocumentRepository : IDocumentRepository
{
    private readonly ConcurrentDictionary<Guid, Document> _documents = new();

    public Task AddAsync(Document document, CancellationToken cancellationToken)
    {
        _documents[document.Id] = document;
        return Task.CompletedTask;
    }

    public Task<Document?> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        _documents.TryGetValue(documentId, out var document);
        return Task.FromResult(document);
    }

    public Task UpdateAsync(Document document, CancellationToken cancellationToken)
    {
        _documents[document.Id] = document;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryDocumentReadModelRepository : IDocumentReadModelRepository
{
    private readonly ConcurrentDictionary<Guid, DocumentReadModel> _documents = new();

    public Task<DocumentReadModel?> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        _documents.TryGetValue(documentId, out var document);
        return Task.FromResult(document);
    }

    public Task<IReadOnlyCollection<DocumentReadModel>> ListByUserAsync(string? uploadedByUserId, CancellationToken cancellationToken)
    {
        var result = string.IsNullOrWhiteSpace(uploadedByUserId)
            ? _documents.Values.ToArray()
            : _documents.Values.Where(document => document.UploadedByUserId.Equals(uploadedByUserId, StringComparison.OrdinalIgnoreCase)).ToArray();

        return Task.FromResult<IReadOnlyCollection<DocumentReadModel>>(result);
    }

    public Task UpsertAsync(DocumentReadModel readModel, CancellationToken cancellationToken)
    {
        _documents[readModel.DocumentId] = readModel;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryDocumentProcessingQueue : IDocumentProcessingQueue
{
    private readonly Channel<DocumentUploadedMessage> _channel = Channel.CreateUnbounded<DocumentUploadedMessage>();

    public IAsyncEnumerable<DocumentUploadedMessage> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask EnqueueAsync(DocumentUploadedMessage message, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(message, cancellationToken);
}

internal sealed class InMemoryDocumentMessagePublisher : IDocumentMessagePublisher
{
    private readonly IDocumentProcessingQueue _queue;

    public InMemoryDocumentMessagePublisher(IDocumentProcessingQueue queue)
    {
        _queue = queue;
    }

    public Task PublishAsync(DocumentUploadedMessage message, CancellationToken cancellationToken) =>
        _queue.EnqueueAsync(message, cancellationToken).AsTask();
}

internal sealed class SignalRDocumentNotificationPublisher : IDocumentNotificationPublisher
{
    private readonly IHubContext<DocumentStatusHub, IDocumentStatusClient> _hubContext;

    public SignalRDocumentNotificationPublisher(IHubContext<DocumentStatusHub, IDocumentStatusClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishStatusChangedAsync(DocumentReadModel readModel, CancellationToken cancellationToken) =>
        _hubContext.Clients.Group(DocumentStatusHub.GroupForUser(readModel.UploadedByUserId))
            .DocumentUpdated(new DocumentRealtimeMessage(
                readModel.DocumentId,
                readModel.Status,
                readModel.FailureReason,
                readModel.HotspotCount,
                readModel.LastUpdatedAtUtc));
}

internal sealed class PythonProcessorClient : IPythonProcessorClient
{
    private readonly HttpClient _httpClient;
    private readonly PythonProcessorOptions _options;

    public PythonProcessorClient(HttpClient httpClient, IOptions<PythonProcessorOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
    }

    public async Task DispatchAsync(DocumentUploadedMessage message, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("/process", message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

internal sealed class QueuedDocumentDispatchBackgroundService : BackgroundService
{
    private readonly IDocumentProcessingQueue _queue;
    private readonly DocumentApplicationService _applicationService;
    private readonly IPythonProcessorClient _pythonProcessorClient;

    public QueuedDocumentDispatchBackgroundService(
        IDocumentProcessingQueue queue,
        DocumentApplicationService applicationService,
        IPythonProcessorClient pythonProcessorClient)
    {
        _queue = queue;
        _applicationService = applicationService;
        _pythonProcessorClient = pythonProcessorClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.DequeueAllAsync(stoppingToken))
        {
            var markResult = await _applicationService.MarkProcessingAsync(message.DocumentId, stoppingToken);
            if (markResult.IsFailure)
            {
                continue;
            }

            try
            {
                await _pythonProcessorClient.DispatchAsync(message, stoppingToken);
            }
            catch (Exception exception)
            {
                await _applicationService.FailProcessingAsync(
                    new FailDocumentProcessingRequest(message.DocumentId, $"Python processor dispatch failed: {exception.Message}"),
                    stoppingToken);
            }
        }
    }
}
