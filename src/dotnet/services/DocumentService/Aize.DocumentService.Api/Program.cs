using Aize.DocumentService.Api;
using Aize.DocumentService.Application;
using Aize.DocumentService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true);
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});

builder.Services.AddSignalR();
builder.Services.AddDocumentInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseCors("frontend");

app.MapGet("/", () => Results.Ok(new
{
    service = "Aize Document Service",
    description = "Ingress, orchestration, queue dispatch, and SignalR notifications for P&ID processing."
}));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/documents/uploads", async (
    HttpRequest request,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var form = await request.ReadFormAsync(cancellationToken);
    var projectName = form["projectName"].ToString();
    var uploadedByUserId = form["uploadedByUserId"].ToString();
    var files = form.Files;

    if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(uploadedByUserId) || files.Count == 0)
    {
        return Results.BadRequest(new
        {
            message = "projectName, uploadedByUserId, and at least one file are required."
        });
    }

    var payloads = files
        .Select(file => new UploadDocumentPayload(
            projectName,
            uploadedByUserId,
            file.FileName,
            file.ContentType,
            file.Length,
            file.OpenReadStream()))
        .ToArray();

    try
    {
        var accepted = await documentService.UploadAsync(payloads, cancellationToken);
        return Results.Accepted($"/api/documents?uploadedByUserId={Uri.EscapeDataString(uploadedByUserId)}", new UploadDocumentsResponse(accepted));
    }
    finally
    {
        foreach (var payload in payloads)
        {
            await payload.Content.DisposeAsync();
        }
    }
});

app.MapGet("/api/documents", async (
    string? uploadedByUserId,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var documents = await documentService.ListByUserAsync(uploadedByUserId, cancellationToken);
    return Results.Ok(documents);
});

app.MapGet("/api/documents/{documentId:guid}", async (
    Guid documentId,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var document = await documentService.GetAsync(documentId, cancellationToken);
    return document is null ? Results.NotFound() : Results.Ok(document);
});

app.MapPost("/api/processing/completed", async (
    CompleteDocumentProcessingApiRequest requestModel,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var result = await documentService.CompleteProcessingAsync(
        new CompleteDocumentProcessingRequest(requestModel.DocumentId, requestModel.Hotspots),
        cancellationToken);

    return result.IsFailure ? Results.NotFound(new { message = result.Error }) : Results.Accepted();
});

app.MapPost("/api/processing/failed", async (
    FailDocumentProcessingApiRequest requestModel,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var result = await documentService.FailProcessingAsync(
        new FailDocumentProcessingRequest(requestModel.DocumentId, requestModel.Reason),
        cancellationToken);

    return result.IsFailure ? Results.NotFound(new { message = result.Error }) : Results.Accepted();
});

app.MapHub<DocumentStatusHub>(DocumentStatusHub.Route);

app.Run();
