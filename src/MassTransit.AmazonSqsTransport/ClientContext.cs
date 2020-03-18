﻿namespace MassTransit.AmazonSqsTransport
{
    using System.Threading;
    using System.Threading.Tasks;
    using Amazon.SimpleNotificationService.Model;
    using Amazon.SQS.Model;
    using Contexts;
    using GreenPipes;
    using Pipeline;
    using Topology;
    using Topology.Entities;


    public interface ClientContext :
        PipeContext,
        IAsyncDisposable
    {
        ConnectionContext ConnectionContext { get; }

        Task<TopicInfo> CreateTopic(Topology.Entities.Topic topic);

        Task<QueueInfo> CreateQueue(Queue queue);

        Task CreateQueueSubscription(Topology.Entities.Topic topic, Queue queue);

        Task DeleteTopic(Topology.Entities.Topic topic);

        Task DeleteQueue(Queue queue);

        Task BasicConsume(ReceiveSettings receiveSettings, IBasicConsumer consumer);

        Task<PublishRequest> CreatePublishRequest(string topicName, byte[] body);

        Task Publish(PublishRequest request, CancellationToken cancellationToken = default);

        Task<SendMessageRequest> CreateSendRequest(string queueName, byte[] body);

        Task SendMessage(SendMessageRequest request, CancellationToken cancellationToken);

        Task DeleteMessage(string queueUrl, string receiptHandle, CancellationToken cancellationToken = default);

        Task PurgeQueue(string queueName, CancellationToken cancellationToken);
    }
}
