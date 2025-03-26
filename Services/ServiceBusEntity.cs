namespace MyApplicationTest.Services
{

    public enum EntityType
    {
        Queue,
        Topic
    }

    public class ServiceBusEntity
    {
        public string Name { get; set; }
        public EntityType Type { get; set; }
        public string SubscriptionName { get; set; } // Only used for Topics
    }

}