open Fake.Core
open Fake.IO
open Farmer
open Farmer.Builders
open Helpers
open Farmer.Arm

initializeContext ()

let sharedPath = Path.getFullName "src/Shared"
let serverPath = Path.getFullName "src/Server"
let clientPath = Path.getFullName "src/Client"
let deployPath = Path.getFullName "deploy"
let sharedTestsPath = Path.getFullName "tests/Shared"
let serverTestsPath = Path.getFullName "tests/Server"
let clientTestsPath = Path.getFullName "tests/Client"

let resGroupName = "Logging-Test"
let adminEmail = "YOUR-ADMIN-EMAIL"
let adminCountryCode = "44" // If in UK
let adminPhone = "YOUR-ADMIN-PHONE-NUMBER"

let scheduledQueryRule
    alertName
    location
    alertDescription
    alertSeverity
    isEnabled
    workspaceResourceId
    evaluationFrequency
    windowSize
    query
    metricMeasureColumn
    resourceIdColumn
    operator
    threshold
    timeAggregation
    numberOfEvaluationPeriods
    minFailingPeriodsToAlert
    muteActionsDuration
    autoMitigate
    checkWorkspaceAlertsStorageConfigured
    actionGroupId =
    $"""{{
        "type": "Microsoft.Insights/scheduledQueryRules",
        "apiVersion": "2021-08-01",
        "name": "{alertName}",
        "location": "{location}",
        "tags": {{}},
        "properties": {{
            "description": "{alertDescription}",
            "severity": "{alertSeverity}",
            "enabled": "{isEnabled}",
            "scopes": [
                "{workspaceResourceId}"
            ],
            "evaluationFrequency": "{evaluationFrequency}",
            "windowSize": "{windowSize}",
            "criteria": {{
                "allOf": [
                    {{
                        "query": "{query}",
                        "metricMeasureColumn": "{metricMeasureColumn |> Option.defaultValue ""}",
                        "resourceIdColumn": "{resourceIdColumn}",
                        "dimensions": [],
                        "operator": "{operator}",
                        "threshold": "{threshold}",
                        "timeAggregation": "{timeAggregation}",
                        "failingPeriods": {{
                            "numberOfEvaluationPeriods": "{numberOfEvaluationPeriods}",
                            "minFailingPeriodsToAlert": "{minFailingPeriodsToAlert}"
                        }}
                    }}
                ]
            }},
            "muteActionsDuration": "{muteActionsDuration}",
            "autoMitigate": "{autoMitigate}",
            "checkWorkspaceAlertsStorageConfigured": "{checkWorkspaceAlertsStorageConfigured}",
            "actions": {{
                "actionGroups": [
                    "{actionGroupId}"
                ],
                "customProperties": {{
                }}
            }}
        }}
    }}"""
    |> Resource.ofJson

let dce dceName location =
    $"""{{
      "type": "Microsoft.Insights/dataCollectionEndpoints",
      "apiVersion": "2023-03-11",
      "name": "{dceName}",
      "location": "{location}",
      "properties": {{
      }}
    }}"""
    |> Resource.ofJson

let serilogDcr // Hard coded Serilog columns
    tableName
    dcrName
    location
    workspaceResourceId
    logWorkspaceName
    streamName
    dceName
    dceResourceId =
    $"""{{
        "type": "microsoft.insights/datacollectionrules",
        "apiVersion": "2023-03-11",
        "dependsOn": [
            "[resourceId('Microsoft.OperationalInsights/workspaces/tables', '{logWorkspaceName}', '{tableName}')]",
            "[resourceId('Microsoft.Insights/dataCollectionEndpoints', '{dceName}')]"
        ],
        "name": "{dcrName}",
        "location": "{location}",
        "tags": {{}},
        "properties": {{
            "dataCollectionEndpointId": "{dceResourceId}",
            "streamDeclarations": {{
                "{streamName}": {{
                    "columns": [
                        {{
                            "name": "TimeGenerated",
                            "type": "datetime"
                        }},
                        {{
                            "name": "Event",
                            "type": "dynamic"
                        }}
                    ]
                }}
            }},
            "dataSources": {{}},
            "destinations": {{
                "logAnalytics": [
                    {{
                        "workspaceResourceId": "{workspaceResourceId}",
                        "name": "{logWorkspaceName}"
                    }}
                ]
            }},
            "dataFlows": [
                {{
                    "streams": [
                        "{streamName}"
                    ],
                    "destinations": [
                        "{logWorkspaceName}"
                    ],
                    "transformKql": "source",
                    "outputStream": "{streamName}"
                }}
            ]
        }}
    }}"""
    |> Resource.ofJson

let serilogTable // Hard coded Serilog columns / Analytics plan
    serilogTableName
    logWorkspaceName
    retentionInDays =
    $"""{{
        "type": "Microsoft.OperationalInsights/workspaces/tables",
        "apiVersion": "2023-09-01",
        "dependsOn": [
            "[resourceId('Microsoft.OperationalInsights/workspaces', '{logWorkspaceName}')]"
        ],
        "name": "{logWorkspaceName}/{serilogTableName}",
        "properties": {{
            "plan": "Analytics",
            "retentionInDays": "{retentionInDays}",
            "schema": {{
                "columns": [
                    {{
                        "name": "TimeGenerated",
                        "type": "datetime"
                    }},
                    {{
                        "name": "Event",
                        "type": "dynamic"
                    }}
                ],
                "name": "{serilogTableName}"
            }},
            "totalRetentionInDays": "{retentionInDays}"
        }}
    }}"""
    |> Resource.ofJson

Target.create "Clean" (fun _ ->
    Shell.cleanDir deployPath
    run dotnet [ "fable"; "clean"; "--yes" ] clientPath // Delete *.fs.js files created by Fable
)

Target.create "RestoreClientDependencies" (fun _ -> run npm [ "ci" ] ".")

Target.create "Bundle" (fun _ ->
    [
        "server", dotnet [ "publish"; "-c"; "Release"; "-o"; deployPath ] serverPath
        "client", dotnet [ "fable"; "-o"; "output"; "-s"; "--run"; "npx"; "vite"; "build" ] clientPath
    ]
    |> runParallel)

Target.create "Azure" (fun _ ->

    let loggingName = "log-analytics"

    let logging = logAnalytics {
        name loggingName
        retention_period 30<Days>
        daily_cap 1<Gb> // Need alert if we hit limit, see below
    }

    let emailReceiver =
       EmailReceiver.Create(
           name="admin-email",
           email = adminEmail
       )

    let smsReceiver =
       SMSReceiver.Create(
           name="admin-sms",
           countryCode = adminCountryCode,
           phoneNumber = adminPhone
       )

    let alertAction = actionGroup {
       name "email-sms-alert"
       short_name "eml-sms-alrt"
       enabled true
       add_email_receivers [ emailReceiver ]
       add_sms_receivers [ smsReceiver ]
    }

    let deployLocation = Location.UKSouth
    let workspaceResourceId = (logging :> IBuilder).ResourceId.Eval()

    let logDataAlert = // See https://learn.microsoft.com/en-us/azure/azure-monitor/logs/daily-cap#alert-when-daily-cap-is-reached
        scheduledQueryRule
            "Daily log data limit reached"
            deployLocation.ArmValue
            "Notify admins if log data limit reached"
            2
            "true"
            workspaceResourceId
            "PT5M"
            "PT5M"
            @"_LogOperation | where Category =~ 'Ingestion' | where Detail contains 'OverQuota'"
            None
            "_ResourceId"
            "GreaterThan"
            0
            "Count"
            1
            1
            "PT5M"
            "false"
            "false"
            alertAction.ActionGroupId

    let serilogTableName = "Serilog"
    let serilogAnalyticsTableName = $"{serilogTableName}_CL"
    let serilogStreamName = $"Custom-{serilogAnalyticsTableName}"

    let logTable = serilogTable serilogAnalyticsTableName loggingName 30

    let dceName = "SerilogDCE"
    let dce = dce dceName deployLocation.ArmValue
    let dceResourceId = $"resourceId('Microsoft.Insights/dataCollectionEndpoints', '{dceName}')"

    let dcrName = "SerilogDCR"
    let dcrResourceId = $"resourceId('Microsoft.Insights/datacollectionrules', '{dcrName}')"

    let dcr =
        serilogDcr
            serilogAnalyticsTableName
            dcrName
            deployLocation.ArmValue
            workspaceResourceId
            loggingName
            serilogStreamName
            dceName
            $"[{dceResourceId}]"

    let insights =
        appInsights {
            name "app-insights"
            log_analytics_workspace logging
        }

    let app = webApp {
        name resGroupName
        operating_system OS.Linux
        runtime_stack (DotNet "8.0")
        zip_deploy "deploy"
        system_identity // Allows the app to auth itself to the DCR
        run_from_package
        link_to_app_insights insights
        setting "APPLICATIONINSIGHTS_CONNECTION_STRING" insights.ConnectionString
        setting "Serilog_DCR_ImmutableId" $"[reference({dcrResourceId}, '2023-03-11').ImmutableId]"
        setting "Serilog_DCE_Endpoint" $"[reference({dceResourceId}, '2023-03-11').LogsIngestion.Endpoint]"
        setting "Serilog_StreamName" serilogStreamName
    }

    let createRoleName (resourceName : string) (principleId : PrincipalId) (roleId : RoleId) =
        $"{resourceName}{principleId.ArmExpression.Value}{roleId.Id}"
        |> DeterministicGuid.create // Copied from Farmer internals https://github.com/CompositionalIT/farmer/blob/5dae58b4930583d20b684b79326ce387bc8b1692/src/Farmer/Types.fs#L495
        |> string
        |> ResourceName

    let metricsPublishingRole =
        {  Name = createRoleName app.Name.ResourceName.Value app.SystemIdentity.PrincipalId Roles.MonitoringMetricsPublisher
           RoleDefinitionId = Roles.MonitoringMetricsPublisher
           PrincipalId = app.SystemIdentity.PrincipalId
           PrincipalType = Arm.RoleAssignment.PrincipalType.ServicePrincipal
           Scope = Arm.RoleAssignment.AssignmentScope.ResourceGroup // Tried to scope only to DCR but failed with unknown resource id
           Dependencies = Set.empty }

    let deployment = arm {
        location deployLocation
        add_resource logging
        add_resource alertAction
        add_resource logDataAlert
        add_resource dce
        add_resource metricsPublishingRole
        add_resource logTable
        add_resource dcr
        add_resource insights
        add_resource app
    }

    deployment
    |> Deploy.execute resGroupName Deploy.NoParameters |> ignore)

Target.create "Run" (fun _ ->
    run dotnet [ "restore"; "Application.sln" ] "."
    run dotnet [ "build" ] sharedPath

    [
        "server", dotnet [ "watch"; "run"; "--no-restore" ] serverPath
        "client", dotnet [ "fable"; "watch"; "-o"; "output"; "-s"; "--run"; "npx"; "vite" ] clientPath
    ]
    |> runParallel)

let buildSharedTests () = run dotnet [ "build" ] sharedTestsPath

Target.create "RunTestsHeadless" (fun _ ->
    buildSharedTests ()

    run dotnet [ "run" ] serverTestsPath
    run dotnet [ "fable"; "-o"; "output" ] clientTestsPath
    run npx [ "mocha"; "output" ] clientTestsPath
)

Target.create "WatchRunTests" (fun _ ->
    buildSharedTests ()

    [
        "server", dotnet [ "watch"; "run" ] serverTestsPath
        "client", dotnet [ "fable"; "watch"; "-o"; "output"; "-s"; "--run"; "npx"; "vite" ] clientTestsPath
    ]
    |> runParallel)

Target.create "Format" (fun _ -> run dotnet [ "fantomas"; "." ] ".")

open Fake.Core.TargetOperators

let dependencies = [
    "Clean" ==> "RestoreClientDependencies" ==> "Bundle" ==> "Azure"

    "Clean" ==> "RestoreClientDependencies" ==> "Run"

    "RestoreClientDependencies" ==> "RunTestsHeadless"
    "RestoreClientDependencies" ==> "WatchRunTests"
]

[<EntryPoint>]
let main args = runOrDefault args