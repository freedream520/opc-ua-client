﻿// Copyright (c) Converter Systems LLC. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Workstation.Collections;
using Workstation.ServiceModel.Ua.Channels;

namespace Workstation.ServiceModel.Ua
{
    /// <summary>
    /// A base class that subscribes to receive data changes and events from an OPC UA server.
    /// </summary>
    public abstract class SubscriptionBase : INotifyPropertyChanged, INotifyDataErrorInfo, ISetDataErrorInfo
    {
        private const double DefaultPublishingInterval = 1000f;
        private const uint DefaultKeepaliveCount = 30;
        private const uint PublishTimeoutHint = 120 * 60 * 1000; // 2 hours

        private readonly ActionBlock<PublishResponse> actionBlock;
        private readonly IProgress<CommunicationState> progress;
        private readonly ILogger logger;
        private volatile bool isPublishing = false;
        private volatile UaTcpSessionChannel innerChannel;
        private volatile uint subscriptionId = 0u;
        private ErrorsContainer<string> errors;
        private PropertyChangedEventHandler propertyChanged;
        private UaApplication application;
        private string endpointName;
        private double publishingInterval = DefaultPublishingInterval;
        private uint keepAliveCount = DefaultKeepaliveCount;
        private uint lifetimeCount = 0u;
        private MonitoredItemBaseCollection monitoredItems = new MonitoredItemBaseCollection();
        private CommunicationState state;
        private CancellationTokenSource stateMachineCts;
        private Task stateMachineTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionBase"/> class.
        /// </summary>
        public SubscriptionBase()
        {
            if (UaApplication.Current == null)
            {
                throw new ArgumentNullException("UaApplication.Current");
            }

            this.application = UaApplication.Current;
            this.application.Completion.ContinueWith(t => this.stateMachineCts?.Cancel());
            this.errors = new ErrorsContainer<string>(p => this.ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(p)));
            this.propertyChanged += this.OnPropertyChanged;
            this.logger = this.application.LoggerFactory?.CreateLogger(this.GetType());
            this.progress = new Progress<CommunicationState>(s => this.State = s);

            // register the action to be run on the ui thread, if there is one.
            if (SynchronizationContext.Current != null)
            {
                this.actionBlock = new ActionBlock<PublishResponse>(pr => this.OnPublishResponse(pr), new ExecutionDataflowBlockOptions { SingleProducerConstrained = true, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() });
            }
            else
            {
                this.actionBlock = new ActionBlock<PublishResponse>(pr => this.OnPublishResponse(pr), new ExecutionDataflowBlockOptions { SingleProducerConstrained = true });
            }

            // read [Subscription] attribute.
            var typeInfo = this.GetType().GetTypeInfo();
            var sa = typeInfo.GetCustomAttribute<SubscriptionAttribute>();
            if (sa != null)
            {
                this.endpointName = sa.EndpointUrl;
                this.publishingInterval = sa.PublishingInterval;
                this.keepAliveCount = sa.KeepAliveCount;
                this.lifetimeCount = sa.LifetimeCount;
            }

            // read [MonitoredItem] attributes.
            foreach (var propertyInfo in typeInfo.DeclaredProperties)
            {
                var mia = propertyInfo.GetCustomAttribute<MonitoredItemAttribute>();
                if (mia == null || string.IsNullOrEmpty(mia.NodeId))
                {
                    continue;
                }

                MonitoringFilter filter = null;
                if (mia.AttributeId == AttributeIds.Value && (mia.DataChangeTrigger != DataChangeTrigger.StatusValue || mia.DeadbandType != DeadbandType.None))
                {
                    filter = new DataChangeFilter() { Trigger = mia.DataChangeTrigger, DeadbandType = (uint)mia.DeadbandType, DeadbandValue = mia.DeadbandValue };
                }

                var propType = propertyInfo.PropertyType;
                if (propType == typeof(DataValue))
                {
                    this.monitoredItems.Add(new DataValueMonitoredItem(
                        target: this,
                        property: propertyInfo,
                        nodeId: NodeId.Parse(mia.NodeId),
                        indexRange: mia.IndexRange,
                        attributeId: mia.AttributeId,
                        samplingInterval: mia.SamplingInterval,
                        filter: filter,
                        queueSize: mia.QueueSize,
                        discardOldest: mia.DiscardOldest));
                    continue;
                }

                if (propType == typeof(BaseEvent) || propType.GetTypeInfo().IsSubclassOf(typeof(BaseEvent)))
                {
                    this.monitoredItems.Add(new EventMonitoredItem(
                        target: this,
                        property: propertyInfo,
                        nodeId: NodeId.Parse(mia.NodeId),
                        indexRange: mia.IndexRange,
                        attributeId: mia.AttributeId,
                        samplingInterval: mia.SamplingInterval,
                        filter: new EventFilter() { SelectClauses = EventHelper.GetSelectClauses(propType) },
                        queueSize: mia.QueueSize,
                        discardOldest: mia.DiscardOldest));
                    continue;
                }

                if (propType == typeof(ObservableQueue<DataValue>))
                {
                    this.monitoredItems.Add(new DataValueQueueMonitoredItem(
                        target: this,
                        property: propertyInfo,
                        nodeId: NodeId.Parse(mia.NodeId),
                        indexRange: mia.IndexRange,
                        attributeId: mia.AttributeId,
                        samplingInterval: mia.SamplingInterval,
                        filter: filter,
                        queueSize: mia.QueueSize,
                        discardOldest: mia.DiscardOldest));
                    continue;
                }

                if (propType.IsConstructedGenericType && propType.GetGenericTypeDefinition() == typeof(ObservableQueue<>))
                {
                    var elemType = propType.GenericTypeArguments[0];
                    if (elemType == typeof(BaseEvent) || elemType.GetTypeInfo().IsSubclassOf(typeof(BaseEvent)))
                    {
                        this.monitoredItems.Add((MonitoredItemBase)Activator.CreateInstance(
                        typeof(EventQueueMonitoredItem<>).MakeGenericType(elemType),
                        this,
                        propertyInfo,
                        NodeId.Parse(mia.NodeId),
                        mia.AttributeId,
                        mia.IndexRange,
                        MonitoringMode.Reporting,
                        mia.SamplingInterval,
                        new EventFilter() { SelectClauses = EventHelper.GetSelectClauses(elemType) },
                        mia.QueueSize,
                        mia.DiscardOldest));
                        continue;
                    }
                }

                this.monitoredItems.Add(new ValueMonitoredItem(
                    target: this,
                    property: propertyInfo,
                    nodeId: NodeId.Parse(mia.NodeId),
                    indexRange: mia.IndexRange,
                    attributeId: mia.AttributeId,
                    samplingInterval: mia.SamplingInterval,
                    filter: filter,
                    queueSize: mia.QueueSize,
                    discardOldest: mia.DiscardOldest));
            }
        }

        /// <inheritdoc/>
        public event PropertyChangedEventHandler PropertyChanged
        {
            add
            {
                var flag = this.propertyChanged.GetInvocationList().Length == 1;
                this.propertyChanged += value;
                if (flag)
                {
                    this.Subscribe();
                }
            }

            remove
            {
                this.propertyChanged -= value;
                if (this.propertyChanged.GetInvocationList().Length == 1)
                {
                    this.Unsubscribe();
                }
            }
        }

        /// <summary>
        /// Gets the <see cref="CommunicationState"/>.
        /// </summary>
        public CommunicationState State
        {
            get { return this.state; }
            private set { this.SetProperty(ref this.state, value); }
        }

        /// <summary>
        /// Gets the inner channel.
        /// </summary>
        protected UaTcpSessionChannel InnerChannel
        {
            get
            {
                if (this.innerChannel == null)
                {
                    throw new ServiceResultException(StatusCodes.BadServerNotConnected);
                }

                return this.innerChannel;
            }
        }

        /// <summary>
        /// This method is automatically called when an object starts listening to the PropertyChanged event.
        /// </summary>
        protected virtual void Subscribe()
        {
            if (this.stateMachineCts == null || this.stateMachineCts.IsCancellationRequested)
            {
                this.stateMachineCts = new CancellationTokenSource();
                this.stateMachineTask = Task.Run(() => this.StateMachineAsync(this.stateMachineCts.Token));
            }
        }

        /// <summary>
        /// This method is automatically called when all objects stop listening to the PropertyChanged event.
        /// </summary>
        protected virtual void Unsubscribe()
        {
            this.stateMachineCts.Cancel();
            try
            {
                this.stateMachineTask.Wait(2000);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Sets the property value and notifies listeners that the property value has changed.
        /// </summary>
        /// <typeparam name="T">Type of the property.</typeparam>
        /// <param name="storage">Reference to a storage field.</param>
        /// <param name="value">The new value.</param>
        /// <param name="propertyName">Name of the property used to notify listeners. This
        /// value is optional and can be provided automatically when invoked from compilers that
        /// support CallerMemberName.</param>
        /// <returns>True if the value changed, otherwise false.</returns>
        protected virtual bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (object.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            this.NotifyPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Notifies listeners that the property value has changed.
        /// </summary>
        /// <param name="propertyName">Name of the property used to notify listeners. This
        /// value is optional and can be provided automatically when invoked from compilers
        /// that support <see cref="T:System.Runtime.CompilerServices.CallerMemberNameAttribute" />.</param>
        protected virtual void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            this.propertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Occurs when the validation errors have changed for a property or entity.
        /// </summary>
        public event EventHandler<DataErrorsChangedEventArgs> ErrorsChanged;

        /// <summary>
        /// Gets a value indicating whether the entity has validation errors.
        /// </summary>
        public bool HasErrors
        {
            get { return this.errors.HasErrors; }
        }

        /// <summary>
        /// Gets the validation errors for a specified property or for the entire entity.
        /// </summary>
        /// <param name="propertyName">The name of the property to retrieve validation errors for, or null or System.String.Empty to retrieve entity-level errors.</param>
        /// <returns>The validation errors for the property or entity.</returns>
        public IEnumerable GetErrors(string propertyName)
        {
            return this.errors.GetErrors(propertyName);
        }

        /// <summary>
        /// Sets the validation errors for a specified property or for the entire entity.
        /// </summary>
        /// <param name="propertyName">The name of the property, or null or System.String.Empty to set entity-level errors.</param>
        /// <param name="errors">The validation errors for the property or entity.</param>
        void ISetDataErrorInfo.SetErrors(string propertyName, IEnumerable<string> errors)
        {
            this.errors.SetErrors(propertyName, errors);
        }

        /// <summary>
        /// Handle PublishResponse message.
        /// </summary>
        /// <param name="publishResponse">The publish response.</param>
        private void OnPublishResponse(PublishResponse publishResponse)
        {
            this.isPublishing = true;
            try
            {
                // loop thru all the notifications
                var nd = publishResponse.NotificationMessage?.NotificationData;
                if (nd == null)
                {
                    return;
                }

                foreach (var n in nd)
                {
                    // if data change.
                    var dcn = n as DataChangeNotification;
                    if (dcn != null)
                    {
                        MonitoredItemBase item;
                        foreach (var min in dcn.MonitoredItems)
                        {
                            if (this.monitoredItems.TryGetValueByClientId(min.ClientHandle, out item))
                            {
                                try
                                {
                                    item.Publish(min.Value);
                                }
                                catch (Exception ex)
                                {
                                    this.logger?.LogError($"Error publishing value for NodeId {item.NodeId}. {ex.Message}");
                                }
                            }
                        }

                        continue;
                    }

                    // if event.
                    var enl = n as EventNotificationList;
                    if (enl != null)
                    {
                        MonitoredItemBase item;
                        foreach (var efl in enl.Events)
                        {
                            if (this.monitoredItems.TryGetValueByClientId(efl.ClientHandle, out item))
                            {
                                try
                                {
                                    item.Publish(efl.EventFields);
                                }
                                catch (Exception ex)
                                {
                                    this.logger?.LogError($"Error publishing event for NodeId {item.NodeId}. {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                this.isPublishing = false;
            }
        }

        /// <summary>
        /// Handles PropertyChanged event. If the property is associated with a MonitoredItem, writes the property value to the node of the server.
        /// </summary>
        /// <param name="sender">the sender.</param>
        /// <param name="e">the event.</param>
        private async void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (this.isPublishing || string.IsNullOrEmpty(e.PropertyName))
            {
                return;
            }

            MonitoredItemBase item;
            if (this.monitoredItems.TryGetValueByName(e.PropertyName, out item))
            {
                DataValue value;
                if (item.TryGetValue(out value))
                {
                    StatusCode statusCode;
                    var writeRequest = new WriteRequest
                    {
                        NodesToWrite = new[] { new WriteValue { NodeId = item.NodeId, AttributeId = item.AttributeId, IndexRange = item.IndexRange, Value = value } }
                    };
                    try
                    {
                        var writeResponse = await this.InnerChannel.WriteAsync(writeRequest).ConfigureAwait(false);
                        statusCode = writeResponse.Results[0];
                    }
                    catch (ServiceResultException ex)
                    {
                        statusCode = ex.StatusCode;
                    }
                    catch (Exception)
                    {
                        statusCode = StatusCodes.BadServerNotConnected;
                    }

                    item.OnWriteResult(statusCode);
                    if (StatusCode.IsBad(statusCode))
                    {
                        this.logger?.LogError($"Error writing value for {item.NodeId}. {StatusCodes.GetDefaultMessage(statusCode)}");
                    }
                }
            }
        }

        /// <summary>
        /// Signals the channel state is Closing.
        /// </summary>
        /// <param name="channel">The session channel. </param>
        /// <param name="token">A cancellation token. </param>
        /// <returns>A task.</returns>
        private async Task WhenChannelClosingAsync(UaTcpSessionChannel channel, CancellationToken token = default(CancellationToken))
        {
            var tcs = new TaskCompletionSource<bool>();
            EventHandler handler = (o, e) =>
            {
                tcs.TrySetResult(true);
            };
            using (token.Register(state => ((TaskCompletionSource<bool>)state).TrySetCanceled(), tcs, false))
            {
                try
                {
                    channel.Closing += handler;
                    if (channel.State == CommunicationState.Opened)
                    {
                        await tcs.Task;
                    }
                }
                finally
                {
                    channel.Closing -= handler;
                }
            }
        }

        /// <summary>
        /// The state machine manages the state of the subscription.
        /// </summary>
        /// <param name="token">A cancellation token.</param>
        /// <returns>A task.</returns>
        private async Task StateMachineAsync(CancellationToken token = default(CancellationToken))
        {
            while (!token.IsCancellationRequested)
            {
                this.progress.Report(CommunicationState.Opening);

                try
                {
                    // get a channel.
                    this.innerChannel = await this.application.GetChannelAsync(this.endpointName, token);

                    try
                    {
                        // create the subscription.
                        var subscriptionRequest = new CreateSubscriptionRequest
                        {
                            RequestedPublishingInterval = this.publishingInterval,
                            RequestedMaxKeepAliveCount = this.keepAliveCount,
                            RequestedLifetimeCount = Math.Max(this.lifetimeCount, 3 * this.keepAliveCount),
                            PublishingEnabled = true
                        };
                        var subscriptionResponse = await this.innerChannel.CreateSubscriptionAsync(subscriptionRequest).ConfigureAwait(true);

                        // link up the dataflow blocks
                        var id = this.subscriptionId = subscriptionResponse.SubscriptionId;
                        var linkToken = this.innerChannel.LinkTo(this.actionBlock, pr => pr.SubscriptionId == id);

                        try
                        {
                            // create the monitored items.
                            var items = this.monitoredItems.ToList();
                            if (items.Count > 0)
                            {
                                var requests = items.Select(m => new MonitoredItemCreateRequest { ItemToMonitor = new ReadValueId { NodeId = m.NodeId, AttributeId = m.AttributeId, IndexRange = m.IndexRange }, MonitoringMode = m.MonitoringMode, RequestedParameters = new MonitoringParameters { ClientHandle = m.ClientId, DiscardOldest = m.DiscardOldest, QueueSize = m.QueueSize, SamplingInterval = m.SamplingInterval, Filter = m.Filter } }).ToArray();
                                var itemsRequest = new CreateMonitoredItemsRequest
                                {
                                    SubscriptionId = id,
                                    ItemsToCreate = requests,
                                };
                                var itemsResponse = await this.innerChannel.CreateMonitoredItemsAsync(itemsRequest);
                                for (int i = 0; i < itemsResponse.Results.Length; i++)
                                {
                                    var item = items[i];
                                    var result = itemsResponse.Results[i];
                                    item.OnCreateResult(result);
                                    if (StatusCode.IsBad(result.StatusCode))
                                    {
                                        this.logger?.LogError($"Error creating MonitoredItem for {item.NodeId}. {StatusCodes.GetDefaultMessage(result.StatusCode)}");
                                    }
                                }
                            }

                            this.progress.Report(CommunicationState.Opened);

                            // wait here until channel is closing, or token cancelled.
                            try
                            {
                                await this.WhenChannelClosingAsync(this.innerChannel, token);
                            }
                            catch
                            {
                            }
                            finally
                            {
                                this.progress.Report(CommunicationState.Closing);
                            }
                        }
                        catch (Exception ex)
                        {
                            this.logger?.LogError($"Error creating MonitoredItems. {ex.Message}");
                            this.progress.Report(CommunicationState.Faulted);
                        }
                        finally
                        {
                            linkToken.Dispose();
                        }

                        if (this.innerChannel.State == CommunicationState.Opened)
                        {
                            try
                            {
                                // delete the subscription.
                                var deleteRequest = new DeleteSubscriptionsRequest
                                {
                                    SubscriptionIds = new uint[] { id }
                                };
                                await this.innerChannel.DeleteSubscriptionsAsync(deleteRequest);
                            }
                            catch (Exception ex)
                            {
                                this.logger?.LogError($"Error deleting subscription. {ex.Message}");
                                await Task.Delay(2000);
                            }
                        }

                        this.progress.Report(CommunicationState.Closed);
                    }
                    catch (Exception ex)
                    {
                        this.logger?.LogError($"Error creating subscription. {ex.Message}");
                        this.progress.Report(CommunicationState.Faulted);
                        await Task.Delay(2000);
                    }
                }
                catch (Exception ex)
                {
                    this.logger?.LogTrace($"Error getting channel. {ex.Message}");
                    this.progress.Report(CommunicationState.Faulted);
                    await Task.Delay(2000);
                }
            }
        }
    }
}
