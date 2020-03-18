namespace MassTransit.SimpleInjectorIntegration.Registration
{
    using System;
    using Context;
    using MassTransit.Registration;
    using ScopeProviders;
    using Scoping;
    using SimpleInjector;


    public class SimpleInjectorRegistrationConfigurator :
        RegistrationConfigurator,
        ISimpleInjectorConfigurator
    {
        readonly Lifestyle _hybridLifestyle;

        public SimpleInjectorRegistrationConfigurator(Container container)
            : base(new SimpleInjectorContainerRegistrar(container))
        {
            Container = container;

            _hybridLifestyle = Lifestyle.CreateHybrid(container.Options.DefaultScopedLifestyle, Lifestyle.Singleton);

            AddMassTransitComponents(Container);

            Container.RegisterInstance<IRegistrationConfigurator>(this);

            Container.RegisterSingleton(() => CreateRegistration(container.GetInstance<IConfigurationServiceProvider>()));
        }

        public Container Container { get; }

        public void AddBus(Func<IBusControl> busFactory)
        {
            AddBus(_ => busFactory());
        }

        public void AddBus(Func<Container, IBusControl> busFactory)
        {
            IBusControl BusFactory()
            {
                var provider = Container.GetInstance<IConfigurationServiceProvider>();

                ConfigureLogContext(provider);

                return busFactory(Container);
            }

            Container.RegisterSingleton(BusFactory);

            Container.RegisterSingleton<IBus>(() => Container.GetInstance<IBusControl>());

            Container.Register(GetSendEndpointProvider, _hybridLifestyle);

            Container.Register(GetPublishEndpoint, _hybridLifestyle);

            Container.RegisterSingleton(() => ClientFactoryProvider(Container.GetInstance<IConfigurationServiceProvider>()));
        }

        public void AddMediator(Action<Container, IReceiveEndpointConfigurator> configure = null)
        {
            IMediator MediatorFactory()
            {
                var provider = Container.GetInstance<IConfigurationServiceProvider>();

                ConfigureLogContext(provider);

                return Bus.Factory.CreateMediator(cfg =>
                {
                    configure?.Invoke(Container, cfg);

                    ConfigureMediator(cfg, provider);
                });
            }

            Container.RegisterSingleton(MediatorFactory);

            Container.RegisterSingleton<IClientFactory>(() => Container.GetInstance<IMediator>());
        }

        ISendEndpointProvider GetSendEndpointProvider()
        {
            return (ISendEndpointProvider)Container.GetConsumeContext() ?? Container.GetInstance<IBus>();
        }

        IPublishEndpoint GetPublishEndpoint()
        {
            return (IPublishEndpoint)Container.GetConsumeContext() ?? Container.GetInstance<IBus>();
        }

        static void AddMassTransitComponents(Container container)
        {
            container.Register<ScopedConsumeContextProvider>(Lifestyle.Scoped);

            container.Register(() => container.GetInstance<ScopedConsumeContextProvider>().GetContext() ?? new MissingConsumeContext(), Lifestyle.Scoped);

            container.RegisterSingleton<IConsumerScopeProvider>(() => new SimpleInjectorConsumerScopeProvider(container));
            container.RegisterSingleton<ISagaRepositoryFactory>(() => new SimpleInjectorSagaRepositoryFactory(container));
            container.RegisterSingleton<IConfigurationServiceProvider>(() => new SimpleInjectorConfigurationServiceProvider(container));
        }
    }
}
