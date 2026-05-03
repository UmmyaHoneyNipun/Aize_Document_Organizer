using System.Security.Claims;
using System.Text.Json.Serialization;
using Aize.DocumentService.Api;
using Aize.DocumentService.Application;
using Aize.DocumentService.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

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
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddDataProtection();
builder.Services.Configure<LocalAuthenticationOptions>(builder.Configuration.GetSection(LocalAuthenticationOptions.SectionName));
builder.Services.AddSingleton<LocalBearerTokenService>();
builder.Services.AddAuthentication(LocalBearerTokenService.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, LocalBearerAuthenticationHandler>(
        LocalBearerTokenService.AuthenticationScheme,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "Aize Document Service",
            Version = "v1",
            Description = "Upload, status, and realtime APIs for the document processing pipeline."
        };
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "Opaque token",
            In = ParameterLocation.Header,
            Description = "Paste the accessToken returned by POST /api/auth/login."
        };

        return Task.CompletedTask;
    });

    options.AddOperationTransformer((operation, context, cancellationToken) =>
    {
        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;
        var allowsAnonymous = endpointMetadata.OfType<IAllowAnonymous>().Any();
        var requiresAuthorization = endpointMetadata.OfType<IAuthorizeData>().Any();

        if (!allowsAnonymous && requiresAuthorization)
        {
            OpenApiSecurityTransformers.AddBearerSecurity(operation);
        }

        return Task.CompletedTask;
    });
});
builder.Services.AddSignalR();
builder.Services.AddDocumentInfrastructure(builder.Configuration);

var app = builder.Build();
var openApiEnabled = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled");
var webRootPath = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var hasFrontendAssets = File.Exists(Path.Combine(webRootPath, "index.html"));

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();

if (hasFrontendAssets)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

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
.AllowAnonymous()
.WithOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("GetHealth")
    .WithSummary("Get the health status of the document service.")
    .AllowAnonymous()
    .WithOpenApi();

app.MapPost("/api/auth/login", (
    LoginApiRequest requestModel,
    LocalBearerTokenService tokenService) =>
{
    var login = tokenService.Login(requestModel);
    return login is null
        ? Results.Unauthorized()
        : Results.Ok(login);
})
.WithName("Login")
.WithSummary("Authenticate with a configured local user and receive a bearer token.")
.AllowAnonymous()
.WithOpenApi();

app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
{
    var userId = user.GetUserId();
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new CurrentUserApiResponse(
        userId,
        user.GetUsername() ?? userId,
        user.GetDisplayName() ?? userId,
        user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray()));
})
.WithName("GetCurrentUser")
.WithSummary("Return the authenticated user profile derived from the bearer token.")
.RequireAuthorization()
.WithOpenApi(OpenApiSecurityTransformers.AddBearerSecurity);

app.MapPost("/api/documents/uploads", async (
    HttpRequest httpRequest,
    ClaimsPrincipal user,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var form = await httpRequest.ReadFormAsync(cancellationToken);
    var uploadedByUserId = user.GetUserId();
    var projectName = form["projectName"].ToString().Trim();
    var files = form.Files;

    if (string.IsNullOrWhiteSpace(uploadedByUserId) ||
        string.IsNullOrWhiteSpace(projectName) ||
        files.Count == 0)
    {
        return Results.BadRequest(new
        {
            message = "An authenticated user, projectName, and at least one file are required."
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
        return Results.Accepted(
            $"/api/documents?uploadedByUserId={Uri.EscapeDataString(uploadedByUserId)}",
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
.DisableAntiforgery()
.RequireAuthorization()
.WithOpenApi(OpenApiSecurityTransformers.AddBearerSecurity);

app.MapGet("/api/documents", async (
    ClaimsPrincipal user,
    string? uploadedByUserId,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var currentUserId = user.GetUserId();
    if (string.IsNullOrWhiteSpace(currentUserId))
    {
        return Results.Unauthorized();
    }

    var isAdmin = user.IsInRole("Admin");
    if (!isAdmin &&
        !string.IsNullOrWhiteSpace(uploadedByUserId) &&
        !string.Equals(uploadedByUserId, currentUserId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Forbid();
    }

    var effectiveUserId = isAdmin ? uploadedByUserId : currentUserId;
    var documents = await documentService.ListByUserAsync(effectiveUserId, cancellationToken);
    return Results.Ok(documents);
})
.WithName("ListDocuments")
.WithSummary("List documents, optionally filtered by uploadedByUserId.")
.RequireAuthorization()
.WithOpenApi(OpenApiSecurityTransformers.AddBearerSecurity);

app.MapGet("/api/documents/{documentId:guid}", async (
    Guid documentId,
    ClaimsPrincipal user,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var currentUserId = user.GetUserId();
    if (string.IsNullOrWhiteSpace(currentUserId))
    {
        return Results.Unauthorized();
    }

    var document = await documentService.GetAsync(documentId, cancellationToken);
    if (document is null)
    {
        return Results.NotFound();
    }

    if (!user.IsInRole("Admin") &&
        !string.Equals(document.UploadedByUserId, currentUserId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Forbid();
    }

    return Results.Ok(document);
})
.WithName("GetDocumentById")
.WithSummary("Get the current status and extracted P&ID analysis for a specific document.")
.RequireAuthorization()
.WithOpenApi(OpenApiSecurityTransformers.AddBearerSecurity);

app.MapPost("/api/processing/completed", async (
    CompleteDocumentProcessingApiRequest requestModel,
    DocumentApplicationService documentService,
    CancellationToken cancellationToken) =>
{
    var result = await documentService.CompleteProcessingAsync(
        new CompleteDocumentProcessingRequest(requestModel.DocumentId, requestModel.Analysis),
        cancellationToken);

    return result.IsFailure ? Results.NotFound(new { message = result.Error }) : Results.Accepted();
})
.WithName("CompleteDocumentProcessing")
.WithSummary("Mark a document as completed and attach extracted P&ID analysis.")
.AllowAnonymous()
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
.AllowAnonymous()
.WithOpenApi();

app.MapHub<DocumentStatusHub>(DocumentStatusHub.Route)
    .RequireAuthorization();

if (hasFrontendAssets)
{
    app.MapFallbackToFile("index.html")
        .AllowAnonymous();
}

app.Run();
