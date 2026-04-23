using Aize.DocumentService.Api;
using Aize.DocumentService.Application;
using Aize.DocumentService.Infrastructure;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Mvc;

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

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddDocumentInfrastructure(builder.Configuration);

var app = builder.Build();
var openApiEnabled = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled");

app.UseCors("frontend");

if (openApiEnabled)
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Aize Document Service v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "Aize Document Service Swagger";
    });
}

app.MapGet("/", () => Results.Ok(new
{
    service = "Aize Document Service",
    description = "Ingress, orchestration, queue dispatch, and SignalR notifications for P&ID processing."
}))
.WithName("GetRoot")
.WithSummary("Get the document service root resource.")
.WithOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("GetHealth")
    .WithSummary("Get the health status of the document service.")
    .WithOpenApi();

app.MapPost("/api/documents/uploads", async (
    [FromForm] UploadDocumentsApiRequest requestModel,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(requestModel.ProjectName) ||
        string.IsNullOrWhiteSpace(requestModel.UploadedByUserId) ||
        requestModel.Files.Count == 0)
    {
        return Results.BadRequest(new
        {
            message = "projectName, uploadedByUserId, and at least one file are required."
        });
    }

    var payloads = requestModel.Files
        .Select(file => new UploadDocumentPayload(
            requestModel.ProjectName,
            requestModel.UploadedByUserId,
            file.FileName,
            file.ContentType,
            file.Length,
            file.OpenReadStream()))
        .ToArray();

    try
    {
        var accepted = await documentService.UploadAsync(payloads, cancellationToken);
        return Results.Accepted(
            $"/api/documents?uploadedByUserId={Uri.EscapeDataString(requestModel.UploadedByUserId)}",
            new UploadDocumentsResponse(accepted));
    }
    finally
    {
        foreach (var payload in payloads)
        {
            await payload.Content.DisposeAsync();
        }
    }
})
.WithName("UploadDocuments")
.WithSummary("Upload one or more P&ID drawings for asynchronous processing.")
.Accepts<UploadDocumentsApiRequest>("multipart/form-data")
.WithOpenApi();

app.MapGet("/api/documents", async (
    string? uploadedByUserId,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var documents = await documentService.ListByUserAsync(uploadedByUserId, cancellationToken);
    return Results.Ok(documents);
})
.WithName("ListDocuments")
.WithSummary("List documents, optionally filtered by uploadedByUserId.")
.WithOpenApi();

app.MapGet("/api/documents/{documentId:guid}", async (
    Guid documentId,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var document = await documentService.GetAsync(documentId, cancellationToken);
    return document is null ? Results.NotFound() : Results.Ok(document);
})
.WithName("GetDocumentById")
.WithSummary("Get the current status and extracted hotspots for a specific document.")
.WithOpenApi();

app.MapPost("/api/processing/completed", async (
    CompleteDocumentProcessingApiRequest requestModel,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var result = await documentService.CompleteProcessingAsync(
        new CompleteDocumentProcessingRequest(requestModel.DocumentId, requestModel.Hotspots),
        cancellationToken);

    return result.IsFailure ? Results.NotFound(new { message = result.Error }) : Results.Accepted();
})
.WithName("CompleteDocumentProcessing")
.WithSummary("Mark a document as completed and attach extracted hotspots.")
.WithOpenApi();

app.MapPost("/api/processing/failed", async (
    FailDocumentProcessingApiRequest requestModel,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var result = await documentService.FailProcessingAsync(
        new FailDocumentProcessingRequest(requestModel.DocumentId, requestModel.Reason),
        cancellationToken);

    return result.IsFailure ? Results.NotFound(new { message = result.Error }) : Results.Accepted();
})
.WithName("FailDocumentProcessing")
.WithSummary("Mark a document as failed during background processing.")
.WithOpenApi();

app.MapHub<DocumentStatusHub>(DocumentStatusHub.Route);

app.Run();
