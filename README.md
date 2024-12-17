# Serilog, OTel, Farmer and SAFE Stack

This modified version of the [SAFE Template](https://safe-stack.github.io/docs/template-overview/) shows how to use hook up Serilog structured logging alongside OTel Metrics / Traces using Farmer.

Some of the infrastructure (log query alerts / DCR / DCE / log tables) [aren't available in Farmer yet](https://github.com/CompositionalIT/farmer/issues/1171) so I have built them with raw ARM using the JSON escape hatch.

## Install pre-requisites

You'll need to install the following pre-requisites in order to build SAFE applications

* [.NET SDK](https://www.microsoft.com/net/download) 8.0 or higher
* [Node 18](https://nodejs.org/en/download/) or higher
* [NPM 9](https://www.npmjs.com/package/npm) or higher

## Deploying the application

You will need to add your Azure Subscription Id plus your admin email and telephone to the top of `Build.fs` (these are used to send the alert email / sms).

There are `Bundle` and `Azure` targets that you can use to package your app and deploy to Azure, respectively:

```bash
dotnet run Bundle
dotnet run Azure
```