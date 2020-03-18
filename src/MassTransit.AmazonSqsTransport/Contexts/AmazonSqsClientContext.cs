namespace MassTransit.AmazonSqsTransport.Contexts
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Amazon.Auth.AccessControlPolicy;
    using Amazon.Auth.AccessControlPolicy.ActionIdentifiers;
    using Amazon.Runtime;
    using Amazon.SimpleNotificationService;
    using Amazon.SimpleNotificationService.Model;
    using Amazon.SQS;
    using Amazon.SQS.Model;
    using Context;
    using Exceptions;
    using GreenPipes;
    using Pipeline;
    using Topology;
    using Topology.Entities;
    using Transports;
    using Util;


    public class AmazonSqsClientContext :
        ScopePipeContext,
        ClientContext
    {
        readonly IAmazonSimpleNotificationService _amazonSns;
        readonly IAmazonSQS _amazonSqs;
        readonly CancellationToken _cancellationToken;
        readonly ConnectionContext _connectionContext;
        readonly object _lock = new object();
        readonly IDictionary<string, string> _queueUrls;
        readonly LimitedConcurrencyLevelTaskScheduler _taskScheduler;
        readonly IDictionary<string, string> _topicArns;

        public AmazonSqsClientContext(ConnectionContext connectionContext, IAmazonSQS amazonSqs, IAmazonSimpleNotificationService amazonSns,
            CancellationToken cancellationToken)
            : base(connectionContext)
        {
            _connectionContext = connectionContext;
            _amazonSqs = amazonSqs;
            _amazonSns = amazonSns;
            _cancellationToken = cancellationToken;

            _taskScheduler = new LimitedConcurrencyLevelTaskScheduler(1);

            _queueUrls = new Dictionary<string, string>();
            _topicArns = new Dictionary<string, string>();
        }

        public Task DisposeAsync(CancellationToken cancellationToken)
        {
            _amazonSqs?.Dispose();
            _amazonSns?.Dispose();

            return TaskUtil.Completed;
        }

        CancellationToken PipeContext.CancellationToken => _cancellationToken;

        ConnectionContext ClientContext.ConnectionContext => _connectionContext;

        public async Task<string> CreateTopic(Topology.Entities.Topic topic)
        {
            lock (_lock)
            {
                if (_topicArns.TryGetValue(topic.EntityName, out var result))
                    return result;
            }

            var request = new CreateTopicRequest(topic.EntityName)
            {
                Attributes = topic.TopicAttributes.ToDictionary(x => x.Key, x => x.Value.ToString()),
                Tags = topic.TopicTags.Select(x => new Tag
                {
                    Key = x.Key,
                    Value = x.Value
                }).ToList()
            };

            TransportLogMessages.CreateTopic(topic.EntityName);

            var response = await _amazonSns.CreateTopicAsync(request, _cancellationToken).ConfigureAwait(false);

            EnsureSuccessfulResponse(response);

            var topicArn = response.TopicArn;

            lock (_lock)
                _topicArns[topic.EntityName] = topicArn;

            await Task.Delay(500, _cancellationToken).ConfigureAwait(false);

            return topicArn;
        }

        public async Task<string> CreateQueue(Queue queue)
        {
            lock (_lock)
            {
                if (_queueUrls.TryGetValue(queue.EntityName, out var result))
                    return result;
            }

            // required to preserve backwards compability
            if (queue.EntityName.EndsWith(".fifo", true, CultureInfo.InvariantCulture) && !queue.QueueAttributes.ContainsKey(QueueAttributeName.FifoQueue))
            {
                LogContext.Warning?.Log("Using '.fifo' suffix without 'FifoQueue' attribute might cause unexpected behavior.");

                queue.QueueAttributes[QueueAttributeName.FifoQueue] = true;
            }

            var request = new CreateQueueRequest(queue.EntityName)
            {
                Attributes = queue.QueueAttributes.ToDictionary(x => x.Key, x => x.Value.ToString()),
                Tags = queue.QueueTags.ToDictionary(x => x.Key, x => x.Value)
            };

            TransportLogMessages.CreateQueue(queue.EntityName);

            var response = await _amazonSqs.CreateQueueAsync(request, _cancellationToken).ConfigureAwait(false);

            EnsureSuccessfulResponse(response);

            var queueUrl = response.QueueUrl;

            lock (_lock)
                _queueUrls[queue.EntityName] = queueUrl;

            await Task.Delay(500, _cancellationToken).ConfigureAwait(false);

            return queueUrl;
        }

        async Task ClientContext.CreateQueueSubscription(Topology.Entities.Topic topic, Queue queue)
        {
            string[] results = await Task.WhenAll(CreateTopic(topic), CreateQueue(queue)).ConfigureAwait(false);
            var topicArn = results[0];
            var queueUrl = results[1];

            Dictionary<string, string> queueAttributes = await _amazonSqs.GetAttributesAsync(queueUrl).ConfigureAwait(false);
            var queueArn = queueAttributes[QueueAttributeName.QueueArn];

            IDictionary<string, object> topicSubscriptionAttributes = topic.TopicSubscriptionAttributes;
            IDictionary<string, object> queueSubscriptionAttributes = queue.QueueSubscriptionAttributes;
            var subscriptionAttributes = new Dictionary<string, string>();
            topicSubscriptionAttributes.ToList().ForEach(x => subscriptionAttributes[x.Key] = x.Value.ToString());
            queueSubscriptionAttributes.ToList().ForEach(x => subscriptionAttributes[x.Key] = x.Value.ToString());

            var subscribeRequest = new SubscribeRequest
            {
                TopicArn = topicArn,
                Endpoint = queueArn,
                Protocol = "sqs",
                Attributes = subscriptionAttributes
            };

            var response = await _amazonSns.SubscribeAsync(subscribeRequest, _cancellationToken).ConfigureAwait(false);

            EnsureSuccessfulResponse(response);

            var sqsQueueArn = queueAttributes[QueueAttributeName.QueueArn];
            var topicArnPattern = topicArn.Substring(0, topicArn.LastIndexOf(':') + 1) + "*";

            queueAttributes.TryGetValue(QueueAttributeName.Policy, out var policyStr);
            var policy = string.IsNullOrEmpty(policyStr) ? new Policy() : Policy.FromJson(policyStr);

            if (!QueueHasTopicPermission(policy, topicArnPattern, sqsQueueArn))
            {
                var statement = new Statement(Statement.StatementEffect.Allow);
                statement.Actions.Add(SQSActionIdentifiers.SendMessage);
                statement.Resources.Add(new Resource(sqsQueueArn));
                statement.Conditions.Add(ConditionFactory.NewSourceArnCondition(topicArnPattern));
                statement.Principals.Add(new Principal("*"));
                policy.Statements.Add(statement);

                var setAttributes = new Dictionary<string, string> {{QueueAttributeName.Policy, policy.ToJson()}};
                await _amazonSqs.SetAttributesAsync(queueUrl, setAttributes).ConfigureAwait(false);
            }
        }

        async Task ClientContext.DeleteTopic(Topology.Entities.Topic topic)
        {
            var topicArn = await CreateTopic(topic).ConfigureAwait(false);

            TransportLogMessages.DeleteTopic(topicArn);

            var response = await _amazonSns.DeleteTopicAsync(topicArn, _cancellationToken).ConfigureAwait(false);

            EnsureSuccessfulResponse(response);
        }

        async Task ClientContext.DeleteQueue(Queue queue)
        {
            var queueUrl = await CreateQueue(queue).ConfigureAwait(false);

            TransportLogMessages.DeleteQueue(queueUrl);

            var response = await _amazonSqs.DeleteQueueAsync(queueUrl, _cancellationToken).ConfigureAwait(false);

            EnsureSuccessfulResponse(response);
        }

        Task ClientContext.BasicConsume(ReceiveSettings receiveSettings, IBasicConsumer consumer)
        {
            string queueUrl;
            lock (_lock)
            {
                if (!_queueUrls.TryGetValue(receiveSettings.EntityName, out queueUrl))
                    throw new ArgumentException($"The queue was unknown: {receiveSettings.EntityName}", nameof(receiveSettings));
            }

            return Task.Factory.StartNew(async () =>
            {
                while (!CancellationToken.IsCancellationRequested)
                {
                    List<Message> messages = await PollMessages(queueUrl, receiveSettings).ConfigureAwait(false);

                    await Task.WhenAll(messages.Select(consumer.HandleMessage)).ConfigureAwait(false);
                }
            }, CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        PublishRequest ClientContext.CreatePublishRequest(string topicName, byte[] body)
        {
            var message = Encoding.UTF8.GetString(body);

            lock (_lock)
            {
                if (_topicArns.TryGetValue(topicName, out var topicArn))
                    return new PublishRequest(topicArn, message);
            }

            throw new ArgumentException($"The topic was unknown: {topicName}", nameof(topicName));
        }

        SendMessageRequest ClientContext.CreateSendRequest(string queueName, byte[] body)
        {
            var message = Encoding.UTF8.GetString(body);

            lock (_lock)
            {
                if (_queueUrls.TryGetValue(queueName, out var queueUrl))
                    return new SendMessageRequest(queueUrl, message);
            }

            throw new ArgumentException($"The queue was unknown: {queueName}", nameof(queueName));
        }

        async Task ClientContext.Publish(PublishRequest request, CancellationToken cancellationToken)
        {
            var response = await _amazonSns.PublishAsync(request, cancellationToken).ConfigureAwait(false);

            EnsureSuccessfulResponse(response);
        }

        async Task ClientContext.SendMessage(SendMessageRequest request, CancellationToken cancellationToken)
        {
            var response = await _amazonSqs.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);

            EnsureSuccessfulResponse(response);
        }

        Task ClientContext.DeleteMessage(string queueName, string receiptHandle, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_queueUrls.TryGetValue(queueName, out var queueUrl))
                    return _amazonSqs.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken);
            }

            throw new ArgumentException($"The queue was unknown: {queueName}", nameof(queueName));
        }

        Task ClientContext.PurgeQueue(string queueName, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_queueUrls.TryGetValue(queueName, out var queueUrl))
                    return _amazonSqs.PurgeQueueAsync(queueUrl, cancellationToken);
            }

            throw new ArgumentException($"The queue was unknown: {queueName}", nameof(queueName));
        }

        static bool QueueHasTopicPermission(Policy policy, string topicArnPattern, string sqsQueueArn)
        {
            IEnumerable<Condition> conditions = policy.Statements
                .Where(s => s.Resources.Any(r => r.Id.Equals(sqsQueueArn)))
                .SelectMany(s => s.Conditions);

            return conditions.Any(c =>
                string.Equals(c.Type, ConditionFactory.ArnComparisonType.ArnLike.ToString(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.ConditionKey, ConditionFactory.SOURCE_ARN_CONDITION_KEY, StringComparison.OrdinalIgnoreCase) &&
                c.Values.Contains(topicArnPattern));
        }

        /// <summary>
        /// SQS can only be polled for 10 messages at a time.
        /// Make multiple poll requests, if necessary, to achieve up to PrefetchCount number of messages
        /// </summary>
        /// <param name="queueUrl">URL for queue to be polled</param>
        /// <param name="receiveSettings"></param>
        /// <returns></returns>
        async Task<List<Message>> PollMessages(string queueUrl, ReceiveSettings receiveSettings)
        {
            const int awsMax = 10;

            var remaining = receiveSettings.PrefetchCount % awsMax;
            var receives = Enumerable.Repeat(awsMax, receiveSettings.PrefetchCount / awsMax).ToList();

            if (remaining > 0)
            {
                receives.Add(remaining);
            }

            var responses = await Task.WhenAll(receives.Select(numberOfMessages => ReceiveMessages(receiveSettings, queueUrl, numberOfMessages))).ConfigureAwait(false);

            return responses.SelectMany(r => r.Messages).ToList();
        }

        async Task<ReceiveMessageResponse> ReceiveMessages(ReceiveSettings receiveSettings, string queueUrl, int maxNumberOfMessages)
        {
            var request = new ReceiveMessageRequest(queueUrl)
            {
                MaxNumberOfMessages = maxNumberOfMessages,
                WaitTimeSeconds = receiveSettings.WaitTimeSeconds,
                AttributeNames = new List<string> {"All"},
                MessageAttributeNames = new List<string> {"All"}
            };

            var response = await _amazonSqs.ReceiveMessageAsync(request, CancellationToken).ConfigureAwait(false);

            EnsureSuccessfulResponse(response);

            return response;
        }

        void EnsureSuccessfulResponse(AmazonWebServiceResponse response)
        {
            const string documentationUri = "https://aws.amazon.com/blogs/developer/logging-with-the-aws-sdk-for-net/";

            var statusCode = response.HttpStatusCode;
            var requestId = response.ResponseMetadata.RequestId;

            if (statusCode >= HttpStatusCode.OK && statusCode < HttpStatusCode.MultipleChoices)
                return;

            throw new AmazonSqsTransportException(
                $"Received unsuccessful response ({statusCode}) from AWS endpoint. See AWS SDK logs ({requestId}) for more details: {documentationUri}");
        }
    }
}
