module Server

open System
open SAFE
open Saturn
open Shared
open Azure.Identity
open Azure.Monitor.OpenTelemetry.AspNetCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Serilog
open Serilog.Sinks.AzureLogAnalytics
open Serilog.Formatting.Compact
open OpenTelemetry.Metrics
open System.Diagnostics.Metrics
open System.Diagnostics
open OpenTelemetry.Trace
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration

let tokenCreds = DefaultAzureCredential() // Use a singleton of this, multiple instances causes issues: https://github.com/Azure/azure-sdk-for-net/issues/43796

let otelNamespace = "OTel.Demo"
let activitySource = new ActivitySource(otelNamespace); // Create an ActivitySource to start activities
let meter = new Meter(otelNamespace) // Create a Meter to spawn the Counter

module Storage =
    let todoCounter = meter.CreateCounter<int>("Todos")
    let todos =
        ResizeArray [
            Todo.create "Create new SAFE project"
            Todo.create "Write your app"
            Todo.create "Ship it!!!"
        ]

    let addTodo todo =
        let delay = Random().Next(1000, 5000)
        async {
            use activity = activitySource.StartActivity "AddTodo" // Kick off an activity to time the operation
            do! Async.Sleep delay // Simulate a long running operation for Trace test

            if Todo.isValid todo.Description then
                todos.Add todo
                todoCounter.Add 1 // Increment the Counter for Metrics test
                return Ok()
            else
                return Error "Invalid todo"
        }

let todosApi (ctx : HttpContext) = {
    getTodos = fun () -> async { return Storage.todos |> List.ofSeq }
    addTodo =
        fun todo -> async {
            let logger = ctx.GetService<ILogger<Todo>>()
            logger.LogInformation("Adding todo at {time}: {description}", DateTime.Now, todo.Description)
            match! Storage.addTodo todo with
            | Ok() -> return Storage.todos |> List.ofSeq
            | Error e -> return failwith e
        }
}

let webApp = Api.make todosApi

let configureServices (services : IServiceCollection) =
    // Set up the Azure Monitor exporter
    // You could also enable the OpenTelemetry Protocol (OTLP) Exporter to send telemetry to other locations, see https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-configuration?tabs=aspnetcore#enable-the-otlp-exporter
    services
        .AddSerilog()
        .ConfigureOpenTelemetryMeterProvider(fun builder -> builder.AddMeter otelNamespace |> ignore) // Add the Meter to the MeterProvider
        .ConfigureOpenTelemetryTracerProvider(fun builder -> builder.AddSource otelNamespace |> ignore) // Add the ActivitySource to the TracerProvider
        .AddOpenTelemetry()
        .UseAzureMonitor()//fun options ->
            // ***
            // IMPORTANT: This worked for logs but would not send metrics or traces, I had to revert to using the App Insights Connection string.
            // It's loaded directly through farmer and not particularly sensitive though
            // https://learn.microsoft.com/en-us/azure/azure-monitor/app/connection-strings?tabs=net#is-the-connection-string-a-secret
            // ***
            // Using system assigned identity rather than storing the connection string in environment variables.
            // See https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-configuration?tabs=aspnetcore#enable-microsoft-entra-id-formerly-azure-ad-authentication
            //options.Credential <- tokenCreds
            //options.ConnectionString <- "InstrumentationKey=00000000-0000-0000-0000-000000000000") // Have to set a dummy string otherwise it won't boot (and can't be in appsettings)
        |> ignore
    services

let configureApp (builder : IApplicationBuilder) =
    let config = builder.ApplicationServices.GetService<IConfiguration>();
    let loggerCredentials =
        let creds = LoggerCredential() // Config set during Farmer deploy
        creds.Endpoint <- config["Serilog_DCE_Endpoint"]
        creds.ImmutableId <- config["Serilog_DCR_ImmutableId"]
        creds.StreamName <- config["Serilog_StreamName"]
        creds.TokenCredential <- tokenCreds
        creds

    let configSettings =
        let settings = ConfigurationSettings()
        settings.MinLogLevel <- Serilog.Events.LogEventLevel.Information
        settings

    Log.Logger <-
        LoggerConfiguration()
            .WriteTo.AzureLogAnalytics(RenderedCompactJsonFormatter(), loggerCredentials, configSettings)
            .WriteTo.Console()
            .CreateLogger();

    builder

let app = application {
    service_config configureServices
    app_config configureApp
    use_router webApp
    memory_cache
    use_static "public"
    use_gzip
}

[<EntryPoint>]
let main _ =
    run app
    0