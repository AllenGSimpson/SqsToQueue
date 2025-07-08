module QueueBridge

open System
open Microsoft.Azure.Functions.Worker
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Amazon
open AwsSqsModule
open AzureQueueModule

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

    // 2. Create clients via helper modules
    let region  = RegionEndpoint.GetBySystemName(regionName)
    use sqs     = AwsSqsModule.createClient accessKey secretAccessKey region
    let aqueue  = AzureQueueModule.getClient azureConn azureQueueName
    do! AzureQueueModule.createIfNotExists aqueue

    // 3. Pull batch from SQS
    task {
        let! resp = AwsSqsModule.receiveMessages sqs queueUrl 10 180
        let messages = resp.Messages
        log.LogInformation("Fetched {Count} msg(s) from SQS.", messages.Count)

        for m in messages do
            try
                do! AzureQueueModule.sendMessage aqueue m.Body
                do! AwsSqsModule.deleteMessage sqs queueUrl m.ReceiptHandle
                log.LogInformation("Moved message {Id}.", m.MessageId)
            with ex ->
                log.LogError(ex, "Failed to move msg {Id}", m.MessageId)
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
