Below is a self‑contained, step‑by‑step playbook you can paste into a ticket or hand directly to your Codex‑based automation. It walks through what must be built, why it matters, and every decision that needs to be made so the implementation team can proceed without coming back for clarifications.

## 1  Situation Recap (current flow & gap)
```
Amazon SES  ─▶ SNS  ─▶ SQS  ───X──▶  (Pinnacle – retired)
                      │
                      └─(should now go here)
Azure Queue Storage   ◀──────────────────────────────
```
- All consumer code was retargeted to Azure Queue Storage (AQS).
Consequently, the downstream micro‑services open an Azure queue expecting messages.

- Upstream sources never switched; SNS still deposits messages in the original Amazon SQS queue.

- Result: Messages accumulate in SQS and never reach the new AQS queues. Production impact is silent data loss.

- Temporary remedy: Poll SQS on a short cadence, push each message to the matching Azure queue, then delete it from SQS. The job will live inside the existing App Service plan so no extra infrastructure is needed.

## 2  High‑level design
| Area | Decision |
| --- | --- |
| Function type |	Timer‑trigger Azure Function (code‑based, not Logic App) for full control and easy deployment to the existing App Service |
| Language / framework | Use the same language as the repo (F# scripts & C# classes). F# works well inside Functions via .fsproj; keeps consistency with sqsTesting.fsx. |
| Trigger schedule |	CRON 0 */1 * * * * (every 60 s) to balance latency and cost. Adjust in host.json. |
| Concurrency model	| Single function instance per run to avoid race conditions in SQS visibility. Scale‑out is possible (Functions consumption plan) but not needed when hosted in an App Service plan. |
| Idempotency |	SQS message IDs are unique; after success we call DeleteMessageAsync so duplicates are extremely unlikely. Azure Queue tolerates duplicates; consumers should already be idempotent. |
| Encoding	| Base‑64 encode outgoing messages (QueueMessageEncoding.Base64) so existing Functions that read AQS continue to work. |
| Secrets	| Store AWS access key, AWS secret, AWS region, SQS queue URL(s), Azure Storage connection string in Application Settings (portal or Bicep). Never in code. |
| Logging & metrics	| Use ILogger. Log counts: fetched, pushed, failed, deleted. Surface custom metrics to App Insights (TrackMetric). |
| Error policy |	For each batch: |

1. Receive N (≤10) messages with visibility timeout > 2 × run interval (e.g., 3 min).

2. For each message individually:

    - Try push to AQS.

    - On success → delete from SQS.

    - On failure → don’t delete; visibility timeout lets retry on next run. |
| Future removal | Keep the function behind a feature flag (SqsBridgeEnabled). When upstream is fixed turn flag off, wait for SQS to drain, then delete. |

## 3  Technical reference for Codex
### 3.1 Folder / project layout
``
/src
 └── BridgeFunctionApp/        <-- new Azure Functions project (.fsproj)
      ├── QueueBridge.fs       <-- timer function
      ├── AwsSqsModule.fs      <-- thin wrapper using AWSSDK.SQS
      ├── AzureQueueModule.fs  <-- thin wrapper using Azure.Storage.Queues
      └── host.json / local.settings.json
```
Add the project to the solution so CI/CD picks it up.

### 3.2 Key NuGet packages
```
Copy
<PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="1.*" OutputItemType="Analyzer"/>
<PackageReference Include="Microsoft.Azure.Functions.Worker"      Version="1.*" />
<PackageReference Include="AWS.SDK.SQS"                           Version="3.*" />
<PackageReference Include="Azure.Storage.Queues"                  Version="12.*" />
``
Tip Use the same major versions as shown in sqsTesting.fsx and AwsSqsClient.cs to avoid binding redirects.

### 3.3 Timer function skeleton (F#)
```
module QueueBridge

open System
open Microsoft.Azure.Functions.Worker
open Microsoft.Extensions.Logging
open Amazon
open Amazon.SQS
open Amazon.SQS.Model
open Azure.Storage.Queues

[<Function("SqsToAzureBridge")>]
let run
    ([<TimerTrigger("0 */1 * * * *")>] timer: TimerInfo,
     context: FunctionContext) =

    let log = context.GetLogger("SqsBridge")

    // 1. Read configuration
    let cfg k = Environment.GetEnvironmentVariable(k)
    let accessKey        = cfg "AWS_ACCESS_KEY"
    let secretAccessKey  = cfg "AWS_SECRET_KEY"
    let regionName       = cfg "AWS_REGION"
    let queueUrl         = cfg "SQS_QUEUE_URL"
    let azureConn        = cfg "AZURE_STORAGE_CONNECTION_STRING"
    let azureQueueName   = cfg "AZURE_QUEUE_NAME"

    if String.IsNullOrWhiteSpace accessKey then
        log.LogError("Missing configuration — aborting.")
    else

    // 2. Create clients (outside hot path scope when converting to DI)
    let region  = RegionEndpoint.GetBySystemName(regionName)
    use sqs     = new AmazonSQSClient(accessKey, secretAccessKey, region)
    let aopts   = QueueClientOptions(MessageEncoding = QueueMessageEncoding.Base64)
    let aqueue  = QueueClient(azureConn, azureQueueName, aopts)
    aqueue.CreateIfNotExists()

    // 3. Pull batch from SQS
    let r = ReceiveMessageRequest(QueueUrl = queueUrl,
                                  MaxNumberOfMessages = 10,
                                  WaitTimeSeconds = 0,
                                  VisibilityTimeout = 180)

    task {
        let! resp = sqs.ReceiveMessageAsync(r)
        let messages = resp.Messages
        log.LogInformation("Fetched {Count} msg(s) from SQS.", messages.Count)

        for m in messages do
            try
                do! aqueue.SendMessageAsync m.Body
                do! sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle)
                log.LogInformation("Moved message {Id}.", m.MessageId)
            with ex ->
                log.LogError(ex, "Failed to move msg {Id}", m.MessageId)
    }
    |> Task.WaitAll
```
Codex can flesh out retries, dependency‑injection startup, and unit tests.

### 3.4 Configuration keys (App Service → Configuration pane)
| Key |	Example |
| --- | --- |
| AWS_ACCESS_KEY |	AKIA... |
| AWS_SECRET_KEY |	w9r/so... |
| AWS_REGION |	us-west-1 |
| SQS_QUEUE_URL |	https://sqs.us-west-1.amazonaws.com/123456789012/prod-email-events-queue |
| AZURE_STORAGE_CONNECTION_STRING |	DefaultEndpointsProtocol=... |
| AZURE_QUEUE_NAME |	prod-email-events-queue |
| SqsBridgeEnabled |	true |

Local development uses the same keys inside local.settings.json (never committed).

### 3.5 CI/CD notes
1. Local test:

    - Functions Core Tools → func start.

    - Use AWS credentials scoped to a test queue.

    - Use Azurite or a dev storage account.

2. Build:
Existing GitHub Action / Azure DevOps pipeline already builds the solution; ensure BridgeFunctionApp/*.fsproj is part of it.

3. Deploy:
Publish profile points to the App Service plan (not a Functions Consumption plan). Deployment slot recommended (staging → swap).

## 4  Operational considerations
Topic	Guidance
VisibilityTimeout	Must exceed maximum function delay + run interval to avoid double processing. Start with 3 min.
Throughput	SQS batch max = 10; timer every 60 s → 600 msg/hr baseline. Increase: lower CRON to 10 s or paginate batches until empty.
Poison messages	If any Azure enqueue fails repeatedly, consider sending the raw body to an Azure -poison queue for manual inspection.
Monitoring	App Insights custom metric SqsBridge/Transferred. Alert when SQS queue depth > 0 for > N minutes.
Cost	Reading from SQS costs $0.0000004 per request. At 60 s cadence × 10 msgs = $1/month. Azure cost is negligible inside existing plan.
Security	IAM user fed to SQS should have Least Privilege: sqs:ReceiveMessage, sqs:DeleteMessage, sqs:GetQueueUrl, sqs:GetQueueAttributes on that queue.
Fail‑safe flag	Wrap entire function body in if (cfg "SqsBridgeEnabled") = "true" so you can disable via portal instantly.

## 5  Long‑term remediation (for management)
- Preferred fix: Reconfigure Amazon SNS subscription to push directly into Azure Service Bus or Azure Event Grid or update the micro‑services to read SQS again.

- Decommission plan: When upstream is corrected, set SqsBridgeEnabled=false, let SQS queue drain (verify ApproximateNumberOfMessages = 0), delete the AWS queue and IAM user, then remove function code.

## 6  “Definition of Done”
1. Function deployed to production App Service; flag on.

2. Messages visible in Azure queue within one minute of appearing in SQS.

3. No duplicate deliveries observed in 24‑hour soak.

4. Alerts set for bridge failure & SQS backlog.

5. Runbook documented: how to disable/enable bridge, rotate secrets, and view logs.