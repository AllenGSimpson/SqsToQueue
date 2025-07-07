module AzureQueueModule

open System.Collections.Concurrent
open System.Threading.Tasks
open Azure
open Azure.Storage.Queues
open Azure.Storage.Queues.Models

/// Internal store so callers can reuse clients rather than
/// creating new connections on each invocation.
let private cache = ConcurrentDictionary<string, QueueClient>()

/// Obtain a `QueueClient` for the given queue name using the
/// provided connection string. The returned client is configured
/// to use Base64 message encoding.
let getClient (connection: string) (queueName: string) : QueueClient =
    cache.GetOrAdd(queueName, fun _ ->
        let opts = QueueClientOptions(MessageEncoding = QueueMessageEncoding.Base64)
        QueueClient(connection, queueName, opts))

/// Create the queue in Azure Storage if it does not already exist.
let createIfNotExists (client: QueueClient) : Task =
    client.CreateIfNotExistsAsync()

/// Send a message to the specified queue.
let sendMessage (client: QueueClient) (message: string) : Task<Response<SendReceipt>> =
    client.SendMessageAsync(message)
