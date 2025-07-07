module AwsSqsModule

open System
open System.Threading.Tasks
open Amazon
open Amazon.SQS
open Amazon.SQS.Model

/// Create an Amazon SQS client from credentials and region information.
let createClient (accessKey:string) (secretKey:string) (region:RegionEndpoint) : IAmazonSQS =
    AmazonSQSClient(accessKey, secretKey, region) :> IAmazonSQS

/// Receive a batch of messages from the given queue.
let receiveMessages (client: IAmazonSQS) (queueUrl:string) (maxMessages:int) (visibilityTimeout:int)
    : Task<ReceiveMessageResponse> =
    let req =
        ReceiveMessageRequest(
            QueueUrl = queueUrl,
            MaxNumberOfMessages = maxMessages,
            WaitTimeSeconds = 0,
            VisibilityTimeout = visibilityTimeout)
    client.ReceiveMessageAsync(req)

/// Delete a message from the queue using its receipt handle.
let deleteMessage (client: IAmazonSQS) (queueUrl:string) (receiptHandle:string)
    : Task<DeleteMessageResponse> =
    client.DeleteMessageAsync(queueUrl, receiptHandle)

/// Dispose the client when no longer needed.
let dispose (client: IAmazonSQS) =
    (client :> IDisposable).Dispose()
