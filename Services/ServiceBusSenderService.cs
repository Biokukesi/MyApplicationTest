namespace MyApplicationTest.Services
{
    using Azure.Messaging.ServiceBus;
    using Azure.Messaging.ServiceBus.Administration;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;

    public class ServiceBusSenderService : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly ServiceBusClient _client;
        private readonly ServiceBusAdministrationClient _adminClient;
        private readonly Dictionary<string, ServiceBusSender> _senders = new();
        public ServiceBusSenderService(string connectionString)
        {
            _connectionString = connectionString;

            var clientOptions = new ServiceBusClientOptions
            {
                TransportType = ServiceBusTransportType.AmqpWebSockets
            };

            _client = new ServiceBusClient(_connectionString, clientOptions);
            _adminClient = new ServiceBusAdministrationClient(_connectionString);
        }

        /// <summary>
        /// Retrieves all available queues in the Service Bus namespace
        /// </summary>
        /// <returns>A collection of queue names</returns>
        public async Task<IEnumerable<string>> GetAvailableQueuesAsync()
        {
            List<string> queueNames = new List<string>();

            try
            {
                var queuesIterator = _adminClient.GetQueuesAsync();
                await foreach (var queue in queuesIterator)
                {
                    queueNames.Add(queue.Name);
                }
            }
            catch (Exception ex)
            {
                // Log the exception
                throw new Exception($"Failed to retrieve queues: {ex.Message}", ex);
            }

            return queueNames;
        public async Task<IEnumerable<string>> GetAvailableTopicsAsync()
        {
            List<string> topicNames = new List<string>();

            try
            {
                var topicsIterator = _adminClient.GetTopicsAsync();
                await foreach (var topic in topicsIterator)
                {
                    topicNames.Add(topic.Name);
                }
            }
            catch (Exception ex)
            {
                // Log the exception
                throw new Exception($"Failed to retrieve topics: {ex.Message}", ex);
            }

            return topicNames;
        }

        //Get available entities, in this case an entity is a queue or a topic
        public async Task<Dictionary<EntityType, IEnumerable<string>>> GetAllAvailableEntitiesAsync()
        {
            var result = new Dictionary<EntityType, IEnumerable<string>>();

            result[EntityType.Queue] = await GetAvailableQueuesAsync();
            result[EntityType.Topic] = await GetAvailableTopicsAsync();

            return result;
        }
        public async Task<string> SendMessageAsync(
            ServiceBusEntity entity,
            string messageContent,
            Dictionary<string, object> properties = null,
            string messageId = null,
            string sessionId = null,
            string correlationId = null,
            TimeSpan? timeToLive = null)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (string.IsNullOrWhiteSpace(messageContent))
                throw new ArgumentNullException(nameof(messageContent));

            if (entity.Type == EntityType.Topic && string.IsNullOrWhiteSpace(entity.Name))
                throw new ArgumentException("Topic name cannot be empty", nameof(entity));

            if (entity.Type == EntityType.Queue && string.IsNullOrWhiteSpace(entity.Name))
                throw new ArgumentException("Queue name cannot be empty", nameof(entity));

            // Create or get cached sender
            string senderKey = GetSenderKey(entity);
            if (!_senders.TryGetValue(senderKey, out ServiceBusSender sender))
            {
                sender = _client.CreateSender(entity.Name);
                _senders.Add(senderKey, sender);
            }

            // Create the message
            var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(messageContent))
            {
                MessageId = messageId ?? Guid.NewGuid().ToString()
            };

            // Add optional properties
            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    message.ApplicationProperties.Add(prop.Key, prop.Value);
                }
            }

            // Set optional parameters if provided
            if (!string.IsNullOrEmpty(sessionId))
            {
                message.SessionId = sessionId;
            }

            if (!string.IsNullOrEmpty(correlationId))
            {
                message.CorrelationId = correlationId;
            }

            if (timeToLive.HasValue)
            {
                message.TimeToLive = timeToLive.Value;
            }

            // Send the message
            await sender.SendMessageAsync(message);
            return message.MessageId;
        }
        public async Task<string> SendObjectAsJsonAsync<T>(
            ServiceBusEntity entity,
            T messageObject,
            Dictionary<string, object> properties = null,
            string messageId = null,
            string sessionId = null,
            string correlationId = null,
            TimeSpan? timeToLive = null)
        {
            if (messageObject == null)
                throw new ArgumentNullException(nameof(messageObject));

            string jsonContent = JsonSerializer.Serialize(messageObject);

            // Add content type property to indicate this is JSON
            properties ??= new Dictionary<string, object>();
            properties["ContentType"] = "application/json";

            return await SendMessageAsync(
                entity,
                jsonContent,
                properties,
                messageId,
                sessionId,
                correlationId,
                timeToLive);
        }
        public async Task<IEnumerable<string>> SendMessageBatchAsync(
            ServiceBusEntity entity,
            IEnumerable<string> messages)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (messages == null || !messages.Any())
                return Enumerable.Empty<string>();

            // Create or get cached sender
            string senderKey = GetSenderKey(entity);
            if (!_senders.TryGetValue(senderKey, out ServiceBusSender sender))
            {
                sender = _client.CreateSender(entity.Name);
                _senders.Add(senderKey, sender);
            }

            // Create the batch
            using ServiceBusMessageBatch messageBatch = await sender.CreateMessageBatchAsync();
            List<string> messageIds = new List<string>();

            foreach (var content in messages)
            {
                var messageId = Guid.NewGuid().ToString();
                var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(content))
                {
                    MessageId = messageId
                };

                if (!messageBatch.TryAddMessage(message))
                {
                    // If the batch is full, send it and create a new one
                    await sender.SendMessagesAsync(messageBatch);
                    messageBatch.Dispose();

                    // Create a new batch
                    using ServiceBusMessageBatch newBatch = await sender.CreateMessageBatchAsync();

                    // Add the message that couldn't fit in the previous batch
                    if (!newBatch.TryAddMessage(message))
                    {
                        throw new Exception($"Message is too large to fit in a batch: {messageId}");
                    }

                    messageIds.Add(messageId);
                }
                else
                {
                    messageIds.Add(messageId);
                }
            }

            // Send the last batch
            if (messageBatch.Count > 0)
            {
                await sender.SendMessagesAsync(messageBatch);
            }

            return messageIds;
        }
        public async Task<long> ScheduleMessageAsync(
            ServiceBusEntity entity,
            string messageContent,
            DateTimeOffset scheduledEnqueueTime,
            Dictionary<string, object> properties = null)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            // Create or get cached sender
            string senderKey = GetSenderKey(entity);
            if (!_senders.TryGetValue(senderKey, out ServiceBusSender sender))
            {
                sender = _client.CreateSender(entity.Name);
                _senders.Add(senderKey, sender);
            }

            // Create the message
            var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(messageContent))
            {
                MessageId = Guid.NewGuid().ToString()
            };

            // Add properties if provided
            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    message.ApplicationProperties.Add(prop.Key, prop.Value);
                }
            }

            // Schedule the message
            return await sender.ScheduleMessageAsync(message, scheduledEnqueueTime);
        }
        public async Task CancelScheduledMessageAsync(ServiceBusEntity entity, long sequenceNumber)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            // Create or get cached sender
            string senderKey = GetSenderKey(entity);
            if (!_senders.TryGetValue(senderKey, out ServiceBusSender sender))
            {
                sender = _client.CreateSender(entity.Name);
                _senders.Add(senderKey, sender);
            }

            await sender.CancelScheduledMessageAsync(sequenceNumber);
        }
        public async Task<bool> EntityExistsAsync(ServiceBusEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            try
            {
                if (entity.Type == EntityType.Queue)
                {
                    return await _adminClient.QueueExistsAsync(entity.Name);
                }
                else
                {
                    return await _adminClient.TopicExistsAsync(entity.Name);
                }
            }
            catch (Exception ex)
            {
                // Log the exception
                throw new Exception($"Failed to check if entity exists: {ex.Message}", ex);
            }
        }
        private string GetSenderKey(ServiceBusEntity entity)
        {
            return entity.Type == EntityType.Queue
                ? $"queue:{entity.Name}"
                : $"topic:{entity.Name}";
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var sender in _senders.Values)
            {
                await sender.DisposeAsync();
            }

            _senders.Clear();
            await _client.DisposeAsync();
        }
    }
}