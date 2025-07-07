module QueueBridge

open System
open Microsoft.Azure.Functions.Worker
open System.Threading.Tasks
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
