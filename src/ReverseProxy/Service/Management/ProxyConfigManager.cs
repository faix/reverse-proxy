// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.ReverseProxy.Abstractions;
using Microsoft.ReverseProxy.RuntimeModel;
using Microsoft.ReverseProxy.Service.HealthChecks;
using Microsoft.ReverseProxy.Service.Proxy;
using Microsoft.ReverseProxy.Service.Proxy.Infrastructure;

namespace Microsoft.ReverseProxy.Service.Management
{
    /// <summary>
    /// Provides a method to apply Proxy configuration changes
    /// by leveraging <see cref="IDynamicConfigBuilder"/>.
    /// Also an Implementation of <see cref="EndpointDataSource"/> that supports being dynamically updated
    /// in a thread-safe manner while avoiding locks on the hot path.
    /// </summary>
    /// <remarks>
    /// This takes inspiration from <a "https://github.com/dotnet/aspnetcore/blob/cbe16474ce9db7ff588aed89596ff4df5c3f62e1/src/Mvc/Mvc.Core/src/Routing/ActionEndpointDataSourceBase.cs"/>.
    /// </remarks>
    internal class ProxyConfigManager : EndpointDataSource, IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly ILogger<ProxyConfigManager> _logger;
        private readonly IProxyConfigProvider _provider;
        private readonly IClusterManager _clusterManager;
        private readonly IRouteManager _routeManager;
        private readonly IEnumerable<IProxyConfigFilter> _filters;
        private readonly IConfigValidator _configValidator;
        private readonly IProxyHttpClientFactory _httpClientFactory;
        private readonly ProxyEndpointFactory _proxyEndpointFactory;
        private readonly ITransformBuilder _transformBuilder;
        private readonly List<Action<EndpointBuilder>> _conventions;
        private readonly IActiveHealthCheckMonitor _activeHealthCheckMonitor;
        private IDisposable _changeSubscription;

        private List<Endpoint> _endpoints;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private IChangeToken _changeToken;

        public ProxyConfigManager(
            ILogger<ProxyConfigManager> logger,
            IProxyConfigProvider provider,
            IClusterManager clusterManager,
            IRouteManager routeManager,
            IEnumerable<IProxyConfigFilter> filters,
            IConfigValidator configValidator,
            ProxyEndpointFactory proxyEndpointFactory,
            ITransformBuilder transformBuilder,
            IProxyHttpClientFactory httpClientFactory,
            IActiveHealthCheckMonitor activeHealthCheckMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _clusterManager = clusterManager ?? throw new ArgumentNullException(nameof(clusterManager));
            _routeManager = routeManager ?? throw new ArgumentNullException(nameof(routeManager));
            _filters = filters ?? throw new ArgumentNullException(nameof(filters));
            _configValidator = configValidator ?? throw new ArgumentNullException(nameof(configValidator));
            _proxyEndpointFactory = proxyEndpointFactory ?? throw new ArgumentNullException(nameof(proxyEndpointFactory));
            _transformBuilder = transformBuilder ?? throw new ArgumentNullException(nameof(transformBuilder));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _activeHealthCheckMonitor = activeHealthCheckMonitor ?? throw new ArgumentNullException(nameof(activeHealthCheckMonitor));

            _conventions = new List<Action<EndpointBuilder>>();
            DefaultBuilder = new ReverseProxyConventionBuilder(_conventions);

            _changeToken = new CancellationChangeToken(_cancellationTokenSource.Token);
        }

        public ReverseProxyConventionBuilder DefaultBuilder { get; }

        // EndpointDataSource

        /// <inheritdoc/>
        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                // The Endpoints needs to be lazy the first time to give a chance to ReverseProxyConventionBuilder to add its conventions.
                // Endpoints are accessed by routing on the first request.
                Initialize();   
                return _endpoints;
            }
        }

        private void Initialize()
        {
            if (_endpoints == null)
            {
                lock (_syncRoot)
                {
                    if (_endpoints == null)
                    {
                        CreateEndpoints();
                    }
                }
            }
        }

        private void CreateEndpoints()
        {
            var routes = _routeManager.GetItems();
            var endpoints = new List<Endpoint>(routes.Count);
            foreach (var existingRoute in routes)
            {
                // Only rebuild the endpoint for modified routes or clusters.
                var endpoint = existingRoute.CachedEndpoint;
                if (endpoint == null)
                {
                    endpoint = _proxyEndpointFactory.CreateEndpoint(existingRoute.Config, _conventions);
                    existingRoute.CachedEndpoint = endpoint;
                }
                endpoints.Add(endpoint);
            }

            UpdateEndpoints(endpoints);
        }

        /// <inheritdoc/>
        public override IChangeToken GetChangeToken() => Volatile.Read(ref _changeToken);

        // IProxyConfigManager

        /// <inheritdoc/>
        public async Task<EndpointDataSource> InitialLoadAsync()
        {
            // Trigger the first load immediately and throw if it fails.
            // We intend this to crash the app so we don't try listening for further changes.
            try
            {
                var config = _provider.GetConfig();
                await ApplyConfigAsync(config);

                if (config.ChangeToken.ActiveChangeCallbacks)
                {
                    _changeSubscription = config.ChangeToken.RegisterChangeCallback(ReloadConfig, this);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unable to load or apply the proxy configuration.", ex);
            }

            // Initial active health check is run in the background.
            _ = _activeHealthCheckMonitor.CheckHealthAsync(_clusterManager.GetItems());
            return this;
        }

        private static void ReloadConfig(object state)
        {
            var manager = (ProxyConfigManager)state;
            _ = manager.ReloadConfigAsync();
        }

        private async Task ReloadConfigAsync()
        {
            _changeSubscription?.Dispose();

            IProxyConfig newConfig;
            try
            {
                newConfig = _provider.GetConfig();
            }
            catch (Exception ex)
            {
                Log.ErrorReloadingConfig(_logger, ex);
                // If we can't load the config then we can't listen for changes anymore.
                return;
            }

            try
            {
                var hasChanged = await ApplyConfigAsync(newConfig);
                lock (_syncRoot)
                {
                    // Skip if changes are signaled before the endpoints are initialized for the first time.
                    // The endpoint conventions might not be ready yet.
                    if (hasChanged && _endpoints != null)
                    {
                        CreateEndpoints();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorApplyingConfig(_logger, ex);
            }

            if (newConfig.ChangeToken.ActiveChangeCallbacks)
            {
                _changeSubscription = newConfig.ChangeToken.RegisterChangeCallback(ReloadConfig, this);
            }
        }

        // Throws for validation failures
        private async Task<bool> ApplyConfigAsync(IProxyConfig config)
        {
            var (configuredRoutes, routeErrors) = await VerifyRoutesAsync(config.Routes, cancellation: default);
            var (configuredClusters, clusterErrors) = await VerifyClustersAsync(config.Clusters, cancellation: default);

            if (routeErrors.Count > 0 || clusterErrors.Count > 0)
            {
                throw new AggregateException("The proxy config is invalid.", routeErrors.Concat(clusterErrors));
            }

            // Update clusters first because routes need to reference them.
            UpdateRuntimeClusters(configuredClusters);
            var routesChanged = UpdateRuntimeRoutes(configuredRoutes);
            return routesChanged;
        }

        private async Task<(IList<ProxyRoute>, IList<Exception>)> VerifyRoutesAsync(IReadOnlyList<ProxyRoute> routes, CancellationToken cancellation)
        {
            if (routes == null)
            {
                return (Array.Empty<ProxyRoute>(), Array.Empty<Exception>());
            }

            var seenRouteIds = new HashSet<string>();
            var configuredRoutes = new List<ProxyRoute>(routes?.Count ?? 0);
            var errors = new List<Exception>();

            foreach (var r in routes)
            {
                if (seenRouteIds.Contains(r.RouteId))
                {
                    errors.Add(new ArgumentException($"Duplicate route {r.RouteId}"));
                    continue;
                }

                var route = r;

                try
                {
                    foreach (var filter in _filters)
                    {
                        route = await filter.ConfigureRouteAsync(route, cancellation);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new Exception($"An exception was thrown from the configuration callbacks for route '{r.RouteId}'.", ex));
                    continue;
                }

                var routeErrors = await _configValidator.ValidateRouteAsync(route);
                if (routeErrors.Count > 0)
                {
                    errors.AddRange(routeErrors);
                    continue;
                }

                configuredRoutes.Add(route);
            }

            if (errors.Count > 0)
            {
                return (null, errors);
            }

            return (configuredRoutes, errors);
        }

        private async Task<(IList<Cluster>, IList<Exception>)> VerifyClustersAsync(IReadOnlyList<Cluster> clusters, CancellationToken cancellation)
        {
            if (clusters == null)
            {
                return (Array.Empty<Cluster>(), Array.Empty<Exception>());
            }

            var seenClusterIds = new HashSet<string>(clusters.Count, StringComparer.OrdinalIgnoreCase);
            var configuredClusters = new List<Cluster>(clusters.Count);
            var errors = new List<Exception>();
            // The IProxyConfigProvider provides a fresh snapshot that we need to reconfigure each time.
            foreach (var c in clusters)
            {
                try
                {
                    if (seenClusterIds.Contains(c.Id))
                    {
                        errors.Add(new ArgumentException($"Duplicate cluster '{c.Id}'."));
                        continue;
                    }

                    seenClusterIds.Add(c.Id);

                    // Don't modify the original
                    var cluster = c;

                    foreach (var filter in _filters)
                    {
                        cluster = await filter.ConfigureClusterAsync(cluster, cancellation);
                    }

                    var clusterErrors = await _configValidator.ValidateClusterAsync(cluster);
                    if (clusterErrors.Count > 0)
                    {
                        errors.AddRange(clusterErrors);
                        continue;
                    }

                    configuredClusters.Add(cluster);
                }
                catch (Exception ex)
                {
                    errors.Add(new ArgumentException($"An exception was thrown from the configuration callbacks for cluster '{c.Id}'.", ex));
                }
            }

            if (errors.Count > 0)
            {
                return (null, errors);
            }

            return (configuredClusters, errors);
        }

        private void UpdateRuntimeClusters(IList<Cluster> newClusters)
        {
            var desiredClusters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var newCluster in newClusters)
            {
                desiredClusters.Add(newCluster.Id);

                _clusterManager.GetOrCreateItem(
                    itemId: newCluster.Id,
                    setupAction: currentCluster =>
                    {
                        // We intentionally do not consider destination changes when updating the cluster Revision.
                        // Revision is used to rebuild routing endpoints which should be unrelated to destinations,
                        // and destinations are the most likely to change.
                        UpdateRuntimeDestinations(newCluster.Destinations, currentCluster.DestinationManager);

                        var currentClusterConfig = currentCluster.Config;

                        var httpClient = _httpClientFactory.CreateClient(new ProxyHttpClientContext {
                            ClusterId = currentCluster.ClusterId,
                            OldOptions = currentClusterConfig?.Options.HttpClient ?? ProxyHttpClientOptions.Empty,
                            OldMetadata = currentClusterConfig?.Options.Metadata,
                            OldClient = currentClusterConfig?.HttpClient,
                            NewOptions = newCluster.HttpClient ?? ProxyHttpClientOptions.Empty,
                            NewMetadata = newCluster.Metadata
                        });

                        var newClusterConfig = new ClusterConfig(newCluster, httpClient);

                        if (currentClusterConfig == null ||
                            currentClusterConfig.HasConfigChanged(newClusterConfig))
                        {
                            currentCluster.Revision++;
                            if (currentClusterConfig == null)
                            {
                                Log.ClusterAdded(_logger, newCluster.Id);
                            }
                            else
                            {
                                Log.ClusterChanged(_logger, newCluster.Id);
                            }

                            // Config changed, so update runtime cluster
                            currentCluster.Config = newClusterConfig;
                        }

                        currentCluster.ForceUpdateDynamicState();
                    });
            }

            foreach (var existingCluster in _clusterManager.GetItems())
            {
                if (!desiredClusters.Contains(existingCluster.ClusterId))
                {
                    // NOTE 1: This is safe to do within the `foreach` loop
                    // because `IClusterManager.GetItems` returns a copy of the list of clusters.
                    //
                    // NOTE 2: Removing the cluster from `IClusterManager` is safe and existing
                    // ASP .NET Core endpoints will continue to work with their existing behavior (until those endpoints are updated)
                    // and the Garbage Collector won't destroy this cluster object while it's referenced elsewhere.
                    Log.ClusterRemoved(_logger, existingCluster.ClusterId);
                    _clusterManager.TryRemoveItem(existingCluster.ClusterId);
                }
            }
        }

        private void UpdateRuntimeDestinations(IReadOnlyDictionary<string, Destination> newDestinations, IDestinationManager destinationManager)
        {
            var desiredDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var newDestination in newDestinations)
            {
                desiredDestinations.Add(newDestination.Key);
                destinationManager.GetOrCreateItem(
                    itemId: newDestination.Key,
                    setupAction: destination =>
                    {
                        var destinationConfig = destination.Config;
                        if (destinationConfig == null || destinationConfig.HasChanged(newDestination.Value))
                        {
                            if (destinationConfig == null)
                            {
                                Log.DestinationAdded(_logger, newDestination.Key);
                            }
                            else
                            {
                                Log.DestinationChanged(_logger, newDestination.Key);
                            }
                            destination.Config = new DestinationConfig(newDestination.Value);
                        }
                    });
            }

            foreach (var existingDestination in destinationManager.GetItems())
            {
                if (!desiredDestinations.Contains(existingDestination.DestinationId))
                {
                    // NOTE 1: This is safe to do within the `foreach` loop
                    // because `IDestinationManager.GetItems` returns a copy of the list of destinations.
                    //
                    // NOTE 2: Removing the endpoint from `IEndpointManager` is safe and existing
                    // clusters will continue to work with their existing behavior (until those clusters are updated)
                    // and the Garbage Collector won't destroy this cluster object while it's referenced elsewhere.
                    Log.DestinationRemoved(_logger, existingDestination.DestinationId);
                    destinationManager.TryRemoveItem(existingDestination.DestinationId);
                }
            }
        }

        private bool UpdateRuntimeRoutes(IList<ProxyRoute> routes)
        {
            var desiredRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = false;

            foreach (var configRoute in routes)
            {
                desiredRoutes.Add(configRoute.RouteId);

                // Note that this can be null, and that is fine. The resulting route may match
                // but would then fail to route, which is exactly what we were instructed to do in this case
                // since no valid cluster was specified.
                var cluster = _clusterManager.TryGetItem(configRoute.ClusterId ?? string.Empty);

                _routeManager.GetOrCreateItem(
                    itemId: configRoute.RouteId,
                    setupAction: route =>
                    {
                        var currentRouteConfig = route.Config;
                        if (currentRouteConfig == null ||
                            currentRouteConfig.HasConfigChanged(configRoute, cluster))
                        {
                            // Config changed, so update runtime route
                            changed = true;
                            route.CachedEndpoint = null; // Recreate
                            if (currentRouteConfig == null)
                            {
                                Log.RouteAdded(_logger, configRoute.RouteId);
                            }
                            else
                            {
                                Log.RouteChanged(_logger, configRoute.RouteId);
                            }

                            var newConfig = BuildRouteConfig(configRoute, cluster, route);
                            route.Config = newConfig;
                            route.ClusterRevision = cluster?.Revision;
                        }
                    });
            }

            foreach (var existingRoute in _routeManager.GetItems())
            {
                if (!desiredRoutes.Contains(existingRoute.RouteId))
                {
                    // NOTE 1: This is safe to do within the `foreach` loop
                    // because `IRouteManager.GetItems` returns a copy of the list of routes.
                    //
                    // NOTE 2: Removing the route from `IRouteManager` is safe and existing
                    // ASP .NET Core endpoints will continue to work with their existing behavior since
                    // their copy of `RouteConfig` is immutable and remains operational in whichever state is was in.
                    Log.RouteRemoved(_logger, existingRoute.RouteId);
                    _routeManager.TryRemoveItem(existingRoute.RouteId);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Applies a new set of ASP .NET Core endpoints. Changes take effect immediately.
        /// </summary>
        /// <param name="endpoints">New endpoints to apply.</param>
        private void UpdateEndpoints(List<Endpoint> endpoints)
        {
            if (endpoints == null)
            {
                throw new ArgumentNullException(nameof(endpoints));
            }

            lock (_syncRoot)
            {
                // These steps are done in a specific order to ensure callers always see a consistent state.

                // Step 1 - capture old token
                var oldCancellationTokenSource = _cancellationTokenSource;

                // Step 2 - update endpoints
                Volatile.Write(ref _endpoints, endpoints);

                // Step 3 - create new change token
                _cancellationTokenSource = new CancellationTokenSource();
                Volatile.Write(ref _changeToken, new CancellationChangeToken(_cancellationTokenSource.Token));

                // Step 4 - trigger old token
                oldCancellationTokenSource?.Cancel();
            }
        }

        private RouteConfig BuildRouteConfig(ProxyRoute source, ClusterInfo cluster, RouteInfo runtimeRoute)
        {
            var transforms = _transformBuilder.Build(source, cluster?.Config?.Options);

            var newRouteConfig = new RouteConfig(
                runtimeRoute,
                source,
                cluster,
                transforms);

            return newRouteConfig;
        }

        public void Dispose()
        {
            _changeSubscription?.Dispose();
        }

        private static class Log
        {
            private static readonly Action<ILogger, string, Exception> _clusterAdded = LoggerMessage.Define<string>(
                  LogLevel.Debug,
                  EventIds.ClusterAdded,
                  "Cluster '{clusterId}' has been added.");

            private static readonly Action<ILogger, string, Exception> _clusterChanged = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.ClusterChanged,
                "Cluster '{clusterId}' has changed.");

            private static readonly Action<ILogger, string, Exception> _clusterRemoved = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.ClusterRemoved,
                "Cluster '{clusterId}' has been removed.");

            private static readonly Action<ILogger, string, Exception> _destinationAdded = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.DestinationAdded,
                "Destination '{destinationId}' has been added.");

            private static readonly Action<ILogger, string, Exception> _destinationChanged = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.DestinationChanged,
                "Destination '{destinationId}' has changed.");

            private static readonly Action<ILogger, string, Exception> _destinationRemoved = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.DestinationRemoved,
                "Destination '{destinationId}' has been removed.");

            private static readonly Action<ILogger, string, Exception> _routeAdded = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.RouteAdded,
                "Route '{routeId}' has been added.");

            private static readonly Action<ILogger, string, Exception> _routeChanged = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.RouteChanged,
                "Route '{routeId}' has changed.");

            private static readonly Action<ILogger, string, Exception> _routeRemoved = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.RouteRemoved,
                "Route '{routeId}' has been removed.");

            private static readonly Action<ILogger, Exception> _errorReloadingConfig = LoggerMessage.Define(
                LogLevel.Error,
                EventIds.ErrorReloadingConfig,
                "Failed to reload config. Unable to listen for future changes.");

            private static readonly Action<ILogger, Exception> _errorApplyingConfig = LoggerMessage.Define(
                LogLevel.Error,
                EventIds.ErrorApplyingConfig,
                "Failed to apply the new config.");

            public static void ClusterAdded(ILogger logger, string clusterId)
            {
                _clusterAdded(logger, clusterId, null);
            }

            public static void ClusterChanged(ILogger logger, string clusterId)
            {
                _clusterChanged(logger, clusterId, null);
            }

            public static void ClusterRemoved(ILogger logger, string clusterId)
            {
                _clusterRemoved(logger, clusterId, null);
            }

            public static void DestinationAdded(ILogger logger, string destinationId)
            {
                _destinationAdded(logger, destinationId, null);
            }

            public static void DestinationChanged(ILogger logger, string destinationId)
            {
                _destinationChanged(logger, destinationId, null);
            }

            public static void DestinationRemoved(ILogger logger, string destinationId)
            {
                _destinationRemoved(logger, destinationId, null);
            }

            public static void RouteAdded(ILogger logger, string routeId)
            {
                _routeAdded(logger, routeId, null);
            }

            public static void RouteChanged(ILogger logger, string routeId)
            {
                _routeChanged(logger, routeId, null);
            }

            public static void RouteRemoved(ILogger logger, string routeId)
            {
                _routeRemoved(logger, routeId, null);
            }

            public static void ErrorReloadingConfig(ILogger logger, Exception ex)
            {
                _errorReloadingConfig(logger, ex);
            }

            public static void ErrorApplyingConfig(ILogger logger, Exception ex)
            {
                _errorApplyingConfig(logger, ex);
            }
        }
    }
}
