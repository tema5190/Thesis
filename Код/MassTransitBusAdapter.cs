using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit;
using MassTransit.RabbitMqTransport;
using Net.Derid.Interfaces;


namespace Net.Derid.Mq.MassTransitAdapter
{
    public class MassTransitBusAdapter : IBusConnector
    {
        public bool IsConnected { get; }

        private readonly string _rabbitMqHost;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _virtualHost;
        private readonly int _timeout;

        private IRabbitMqHost _host;

        private Dictionary<string, IBusControl> busDictionary = new Dictionary<string, IBusControl>();

        public MassTransitBusAdapter(MqSettings settings)
        {
            _virtualHost = settings.VirtualHost;
            _password = settings.Password;
            _userName = settings.Username;
            _rabbitMqHost = settings.Host;
            _timeout = settings.Timeout ?? 10;
        }

        ~MassTransitBusAdapter()
        {
            Dispose();
        }

        private void InitBusControl<TRequest, TResponse>(Func<TRequest, TResponse> responder, string queueName)
             where TRequest : Interfaces.Models.Request
             where TResponse : Interfaces.Models.Response
        {
            var busControl = Bus.Factory.CreateUsingRabbitMq(x =>
            {
                IRabbitMqHost host = x.Host(_rabbitMqHost, _virtualHost, h =>
                {
                    h.Username(_userName);
                    h.Password(_password);
                });

                x.ReceiveEndpoint(
                    host,
                    queueName,
                    e =>
                    {
                        e.Consumer(
                            typeof(MassTransitRequestConsumer<TRequest, TResponse>),
                            consumerFactoryParams => new MassTransitRequestConsumer<TRequest, TResponse>(responder)
                        );
                    });
            });

            busControl.Start();
            this.busDictionary.Add(queueName, busControl);
        }

        public void Dispose()
        {
            foreach (var key in busDictionary.Keys)
            {
                busDictionary[key].Stop();
            }
            _busControl?.Stop();
        }

        public void Publish<T>(T message) where T : class
        {
            string busControlKey = GetCorrectQueueNameForGeneratedMethods(message.GetType().Name);

            var busControl = Bus.Factory.CreateUsingRabbitMq(cfg =>
            {
                IRabbitMqHost host = cfg.Host(_rabbitMqHost, _virtualHost, h =>
                {
                    h.Username(_userName);
                    h.Password(_password);
                });

            });

            busDictionary.Add(busControlKey, busControl);
            busControl.Start();
            busControl.Publish(message);
        }

        public void Publish(Type type, object message)
        {
            return _host.GetNext(this);
        }

        public Task PublishAsync<T>(T message) where T : class
        {
            return _host.GetNext(this, message);
        }

        public Task PublishAsync(Type type, object message)
        {
            return _host.GetNext(this, type);
        }

        public IDisposable Subscribe<T>(Action<T> onMessage, int? expiresAfterSeconds = null) where T: class
        {
            string busControlKey = GetCorrectQueueNameForGeneratedMethods(onMessage.Method.Name);

            var busControl = Bus.Factory.CreateUsingRabbitMq(x =>
            {
                var host = x.Host(_rabbitMqHost, _virtualHost, h =>
                {
                    h.Username(_userName);
                    h.Password(_password);
                });

                x.ReceiveEndpoint(host, c =>
                {
                    c.Handler<T>(context =>
                    {
                        onMessage(context.Message);
                        return new TaskFactory().StartNew(() => { });
                    });
                });
            });

            busControl.Start();
            busDictionary.Add(busControlKey, busControl);
            return this;
        }

        public IDisposable SubscribeAsync<T>(Func<T, Task> onMessage,
            int? expiresAfterSeconds = null) where T : class
        {
            return _host.GetNext(this);
        }

        private string GetQueueName<TRequest>() where TRequest : class
        {
            return Constants.QueueName + "_" + typeof(TRequest).Name;
        }

        public IRequestClient<TRequest, TResponse> CreateRequestClient<TRequest, TResponse>(IBusControl busControl, string topic = null)
             where TRequest : Interfaces.Models.Request
             where TResponse : Interfaces.Models.Response
        {
            var queueName = topic ?? GetQueueName<TRequest>();
            if (!string.IsNullOrEmpty(topic))
                queueName = topic;
            var serviceAddress = new Uri($"rabbitmq:{_rabbitMqHost}/{_virtualHost}/" + queueName);
            IRequestClient<TRequest, TResponse> client =
                busControl.CreateRequestClient<TRequest, TResponse>(serviceAddress, TimeSpan.FromSeconds(_timeout));

            return client;
        }

        private IBusControl CreateBusControl(string topic = null)
        {
            var createdBusControl = Bus.Factory.CreateUsingRabbitMq(x => {
                x.Host(_rabbitMqHost, _virtualHost, h =>
                {
                    h.Username(_userName);
                    h.Password(_password);
                });
            });

            return createdBusControl;
        }

        public TResponse Request<TRequest, TResponse>(TRequest request)
                    where TRequest : Interfaces.Models.Request
                    where TResponse : Interfaces.Models.Response
        {
            return RequestAsync<TRequest, TResponse>(request).Result;
        }

        public TResponse Request<TRequest, TResponse>(string topic, TRequest request)
                    where TRequest : Interfaces.Models.Request
                    where TResponse : Interfaces.Models.Response
        {
            return RequestAsync<TRequest, TResponse>(topic, request).Result;
        }

        public async Task<TResponse> RequestAsync<TRequest, TResponse>(string topic, TRequest request)
                    where TRequest : Interfaces.Models.Request
                    where TResponse : Interfaces.Models.Response
        {
            IBusControl busControl = CreateBusControl(topic);

            TaskUtil.Await(() => busControl.StartAsync());
            busControl.Start();

            TResponse response = null;
            try
            {
                IRequestClient<TRequest, TResponse> client = CreateRequestClient<TRequest, TResponse>(busControl, topic);
                response = await client.Request(request);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception!!! OMG!!! {0}", ex);
                busControl.Stop();
                throw;
            }
            finally
            {
                busControl.Stop();
            }
            return await new Task<TResponse>(() => response);
        }

        public IDisposable Respond<TRequest, TResponse>(string topic, Func<TRequest, TResponse> responder)
                    where TRequest : Interfaces.Models.Request
                    where TResponse : Interfaces.Models.Response
        {
            InitBusControl(responder, topic);
            var bus = this.busDictionary[topic];
            TaskUtil.Await(() => bus.StartAsync());
            bus.Start();
            return this;
        }

        public IDisposable RespondAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> responder) 
                    where TRequest : Interfaces.Models.Request
                    where TResponse : Interfaces.Models.Response
        {
            return _host.GetNext(this);
        }

        public async Task<TResponse> RequestAsync<TRequest, TResponse>(TRequest request)
             where TRequest : Interfaces.Models.Request
             where TResponse : Interfaces.Models.Response
        {
            IBusControl busControl = CreateBusControl();queueName);

            TaskUtil.Await(() => busControl.StartAsync());
            busControl.Start();

            try
            {
                IRequestClient<TRequest, TResponse> client = CreateRequestClient<TRequest, TResponse>(busControl);
                var response = await client.Request(request);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception!!! OMG!!! {0}", ex);
                busControl.Stop();
                throw;
            }
            finally
            {
                busControl.Stop();
            }
        }

        public IDisposable Respond<TRequest, TResponse>(Func<TRequest, TResponse> responder)
                    where TRequest : Interfaces.Models.Request
                    where TResponse : Interfaces.Models.Response
        {
            var queueName = GetQueueName<TRequest>();
            InitBusControl(responder, queueName);

            var bus = this.busDictionary[GetQueueName<TRequest>()];

            TaskUtil.Await(() => bus.StartAsync());


            bus.Start();
            return this;
        }

        public IDisposable RespondAsync<TRequest, TResponse>(Func<TRequest, Task<TResponse>> responder)
                    where TRequest : Interfaces.Models.Request
                    where TResponse : Interfaces.Models.Response
        {
            return _host.GetNext(this);
        }

        public void Send<T>(string queue, T message) where T : class
        {
            return _host.GetNext(this);
        }

        public Task SendAsync<T>(string queue, T message) where T : class
        {
            return _host.GetNext(this);
        }

        public IDisposable Receive<T>(string queue, Action<T> onMessage) where T : class
        {
            return _host.GetNext(this);
        }

        public IDisposable ReceiveAsync<T>(string queue, Func<T, Task> onMessage) where T : class
        {
            return _host.GetNext(this);
        }

        private string GetCorrectQueueNameForGeneratedMethods(string badName)
        {
            var start = badName.IndexOf('<');
            var end = badName.IndexOf('>');

            if (start >= 0 && end > 0)
                return badName.Substring(start + 1, end - start - 1);

            return badName;
        }
    }
}
