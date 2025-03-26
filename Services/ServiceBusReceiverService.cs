//Receive messages from a Service Bus entity using Azure.Messaging.ServiceBus
namespace MyApplicationTest.Services
{
    using Azure.Messaging.ServiceBus;
    using Azure.Messaging.ServiceBus.Administration;
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    public class ReceivedMessage
    {
        public string MessageId { get; set; }
        public string Content { get; set; }
        public string EntityName { get; set; }
        public EntityType EntityType { get; set; }
        public DateTime ReceivedTime { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public ServiceBusReceivedMessage ServiceBusReceivedMessage { get; set; } // Added for message completion
        public bool IsCompleted { get; set; } // Added to track completion status
    }

    public class ServiceBusReceiverService : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly ServiceBusClient _client;
        private readonly ServiceBusAdministrationClient _adminClient;
        private readonly Dictionary<string, ServiceBusProcessor> _processors = new();
        private readonly Dictionary<string, ServiceBusReceiver> _receivers = new(); // Added for message completion
        private readonly List<ReceivedMessage> _receivedMessages = new();

        public event EventHandler<ReceivedMessage> MessageReceived;

        public ServiceBusReceiverService(string connectionString)
        {
            _connectionString = connectionString;

            var clientOptions = new ServiceBusClientOptions
            {
                TransportType = ServiceBusTransportType.AmqpWebSockets
            };

            _client = new ServiceBusClient(_connectionString, clientOptions);
            _adminClient = new ServiceBusAdministrationClient(_connectionString);
        }

        public async Task<IEnumerable<ServiceBusEntity>> GetAvailableEntitiesAsync()
        {
            var entities = new List<ServiceBusEntity>();

            try
            {
                var queuesIterator = _adminClient.GetQueuesAsync();
                await foreach (var queue in queuesIterator)
                {
                    entities.Add(new ServiceBusEntity
                    {
                        Name = queue.Name,
                        Type = EntityType.Queue
                    });
                }

                var topicsIterator = _adminClient.GetTopicsAsync();
                await foreach (var topic in topicsIterator)
                {
                    var subscriptionsIterator = _adminClient.GetSubscriptionsAsync(topic.Name);
                    await foreach (var subscription in subscriptionsIterator)
                    {
                        entities.Add(new ServiceBusEntity
                        {
                            Name = topic.Name,
                            Type = EntityType.Topic,
                            SubscriptionName = subscription.SubscriptionName
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching Service Bus entities: {ex.Message}");
                throw;
            }

            return entities;
        }

        public async Task StartReceivingAsync(ServiceBusEntity entity, ServiceBusReceiveMode receiveMode)
        {
            string processorKey = entity.Type == EntityType.Queue
                ? $"queue:{entity.Name}"
                : $"topic:{entity.Name}:subscription:{entity.SubscriptionName}";

            if (_processors.ContainsKey(processorKey))
            {
                throw new InvalidOperationException($"Already receiving messages from {processorKey}");
            }

            // Create a receiver for this entity for completing messages
            if (entity.Type == EntityType.Queue)
            {
                _receivers[processorKey] = _client.CreateReceiver(entity.Name, new ServiceBusReceiverOptions
                {
                    ReceiveMode = receiveMode
                });
            }
            else
            {
                _receivers[processorKey] = _client.CreateReceiver(entity.Name, entity.SubscriptionName, new ServiceBusReceiverOptions
                {
                    ReceiveMode = receiveMode
                });
            }

            var options = new ServiceBusProcessorOptions
            {
                ReceiveMode = receiveMode,
                MaxConcurrentCalls = 1,
                AutoCompleteMessages = false
            };

            ServiceBusProcessor processor = entity.Type == EntityType.Queue
                ? _client.CreateProcessor(entity.Name, options)
                : _client.CreateProcessor(entity.Name, entity.SubscriptionName, options);

            processor.ProcessMessageAsync += async (args) =>
                await ProcessMessageAsync(args, entity);
            processor.ProcessErrorAsync += ProcessErrorAsync;

            await processor.StartProcessingAsync();
            _processors.Add(processorKey, processor);
        }

        public async Task StopReceivingAsync(ServiceBusEntity entity)
        {
            string processorKey = entity.Type == EntityType.Queue
                ? $"queue:{entity.Name}"
                : $"topic:{entity.Name}:subscription:{entity.SubscriptionName}";

            if (_processors.TryGetValue(processorKey, out var processor))
            {
                await processor.StopProcessingAsync();
                await processor.DisposeAsync();
                _processors.Remove(processorKey);
            }

            if (_receivers.TryGetValue(processorKey, out var receiver))
            {
                await receiver.DisposeAsync();
                _receivers.Remove(processorKey);
            }
        }

        public async Task CompleteMessageAsync(ServiceBusEntity entity, ReceivedMessage message)
        {
            if (message.ServiceBusReceivedMessage != null && !message.IsCompleted)
            {
                string processorKey = entity.Type == EntityType.Queue
                    ? $"queue:{entity.Name}"
                    : $"topic:{entity.Name}:subscription:{entity.SubscriptionName}";

                if (_receivers.TryGetValue(processorKey, out var receiver))
                {
                    await receiver.CompleteMessageAsync(message.ServiceBusReceivedMessage);
                    message.IsCompleted = true;
                }
            }
        }

        private async Task ProcessMessageAsync(ProcessMessageEventArgs args, ServiceBusEntity entity)
        {
            try
            {
                var messageBody = Encoding.UTF8.GetString(args.Message.Body);
                var properties = new Dictionary<string, object>();
                foreach (var prop in args.Message.ApplicationProperties)
                {
                    properties.Add(prop.Key, prop.Value);
                }

                var receivedMessage = new ReceivedMessage
                {
                    MessageId = args.Message.MessageId,
                    Content = messageBody,
                    EntityName = entity.Type == EntityType.Queue ? entity.Name : $"{entity.Name}/{entity.SubscriptionName}",
                    EntityType = entity.Type,
                    ReceivedTime = DateTime.Now,
                    Properties = properties,
                    ServiceBusReceivedMessage = args.Message, // Store the original message for later completion
                    IsCompleted = false
                };

                _receivedMessages.Add(receivedMessage);
                MessageReceived?.Invoke(this, receivedMessage);

                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing message: {ex.Message}");
                await args.AbandonMessageAsync(args.Message);
            }
        }

        private Task ProcessErrorAsync(ProcessErrorEventArgs args)
        {
            Console.WriteLine($"Error in Service Bus processor: {args.Exception.Message}");
            Console.WriteLine($"Entity path: {args.EntityPath}");
            return Task.CompletedTask;
        }

        public IReadOnlyList<ReceivedMessage> ReceivedMessages => _receivedMessages.AsReadOnly();

        public async ValueTask DisposeAsync()
        {
            foreach (var processor in _processors.Values)
            {
                await processor.StopProcessingAsync();
                await processor.DisposeAsync();
            }

            foreach (var receiver in _receivers.Values)
            {
                await receiver.DisposeAsync();
            }

            _processors.Clear();
            _receivers.Clear();
            await _client.DisposeAsync();
        }
    }
}