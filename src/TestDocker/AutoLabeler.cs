using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
namespace DockerAutoLabeler
{
    public class AutoLabelerService
    {
        private readonly ILogger<AutoLabelerService> _logger;
        private readonly DockerClient _dockerClient;
        private readonly string _userId;
        private readonly string _domain;
        private readonly string _traefikNetwork;

        public AutoLabelerService(ILogger<AutoLabelerService> logger)
        {
            _logger = logger;
            _userId = Environment.GetEnvironmentVariable("USER_ID") ?? "user";
            _domain = Environment.GetEnvironmentVariable("DOMAIN") ?? "localhost";
            _traefikNetwork = Environment.GetEnvironmentVariable("TRAEFIK_NETWORK") ?? "traefik-public";

            var dockerHost = Environment.GetEnvironmentVariable("DIND_HOST") ?? "unix:///var/run/docker.sock";
            _dockerClient = new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();

            _logger.LogInformation("Auto-Labeler initialized for user: {UserId}", _userId);
            _logger.LogInformation("Domain: {Domain}", _domain);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Auto-Labeler service...");

            // Label existing containers first
            await LabelExistingContainersAsync(cancellationToken);

            // Watch for new container events
            await WatchContainerEventsAsync(cancellationToken);
        }

        private async Task LabelExistingContainersAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Scanning existing containers...");

            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = false },
                cancellationToken
            );

            foreach (var container in containers)
            {
                // Skip if already has traefik labels
                if (container.Labels?.ContainsKey("traefik.enable") == true)
                {
                    _logger.LogInformation("Container {Name} already labeled, skipping", 
                        container.Names.FirstOrDefault());
                    continue;
                }

                // Skip if no port mappings
                if (container.Ports == null || !container.Ports.Any())
                {
                    _logger.LogInformation("Container {Name} has no port mappings, skipping",
                        container.Names.FirstOrDefault());
                    continue;
                }

                await ProcessContainerAsync(container.ID, cancellationToken);
            }
        }

        private async Task WatchContainerEventsAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Watching Docker events for user {UserId}...", _userId);

            var eventParameters = new ContainerEventsParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["type"] = new Dictionary<string, bool> { ["container"] = true },
                    ["event"] = new Dictionary<string, bool> { ["start"] = true }
                }
            };

            var progress = new Progress<Message>(async message =>
            {
                if (message.Action == "start")
                {
                    _logger.LogInformation("New container detected: {ContainerId}", message.ID);
                    await ProcessContainerAsync(message.ID, cancellationToken);
                }
            });

            await _dockerClient.System.MonitorEventsAsync(eventParameters, progress, cancellationToken);
        }

        private async Task ProcessContainerAsync(string containerId, CancellationToken cancellationToken)
        {
            try
            {
                var container = await _dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);

                // Skip if already labeled
                if (container.Config?.Labels?.ContainsKey("traefik.enable") == true)
                {
                    return;
                }

                // Skip if no port bindings
                var ports = container.NetworkSettings?.Ports;
                if (ports == null || !ports.Any() || ports.All(p => p.Value == null || !p.Value.Any()))
                {
                    _logger.LogInformation("Container {Name} has no port mappings, skipping", container.Name);
                    return;
                }

                await ApplyLabelsAsync(container, cancellationToken);
            }
            catch (DockerApiException ex)
            {
                _logger.LogError(ex, "Docker API error processing container {ContainerId}", containerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing container {ContainerId}", containerId);
            }
        }

        private async Task ApplyLabelsAsync(ContainerInspectResponse container, CancellationToken cancellationToken)
        {
            try
            {
                var labels = GenerateTraefikLabels(container);
                var containerName = container.Name?.TrimStart('/') ?? "unknown";

                _logger.LogInformation("Generated labels for {ContainerName}:", containerName);
                foreach (var label in labels)
                {
                    _logger.LogInformation("  {Key}: {Value}", label.Key, label.Value);
                }

                // Connect to Traefik network if not already connected
                await ConnectToTraefikNetworkAsync(container.ID, cancellationToken);

                // Note: Docker API doesn't support updating labels on running containers
                // In production, you would:
                // 1. Store labels in external database/config
                // 2. Use Traefik File provider with dynamic config
                // 3. Or implement container recreation logic

                _logger.LogInformation("✓ Processed container: {ContainerName} -> {UserId}.{Domain}",
                    containerName, _userId, _domain);

                // Print accessible URLs
                PrintAccessibleUrls(container);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying labels to container {ContainerName}", container.Name);
            }
        }

        private Dictionary<string, string> GenerateTraefikLabels(ContainerInspectResponse container)
        {
            var labels = new Dictionary<string, string>
            {
                ["traefik.enable"] = "true",
                ["traefik.docker.network"] = _traefikNetwork
            };

            var ports = container.NetworkSettings?.Ports;
            if (ports == null) return labels;

            var containerName = container.Name?.TrimStart('/') ?? "app";

            foreach (var portMapping in ports)
            {
                if (portMapping.Value == null || !portMapping.Value.Any())
                    continue;

                // Extract port number from format like "80/tcp"
                var containerPort = portMapping.Key.Split('/')[0];
                var hostPort = portMapping.Value[0].HostPort ?? containerPort;

                // Create unique service name
                var serviceName = $"{_userId}-{containerName}-{containerPort}".Replace("/", "-");

                // Subdomain for this user
                var subdomain = $"{_userId}.{_domain}";

                // Routing rule
                string rule;
                string middleware = "";

                if (hostPort == "80")
                {
                    rule = $"Host(`{subdomain}`)";
                }
                else
                {
                    rule = $"Host(`{subdomain}`) && PathPrefix(`/port{hostPort}`)";
                    middleware = $"{serviceName}-stripprefix";

                    // Add strip prefix middleware
                    labels[$"traefik.http.middlewares.{middleware}.stripprefix.prefixes"] = $"/port{hostPort}";
                }

                // Router configuration
                labels[$"traefik.http.routers.{serviceName}.rule"] = rule;
                labels[$"traefik.http.routers.{serviceName}.entrypoints"] = "web";
                labels[$"traefik.http.services.{serviceName}.loadbalancer.server.port"] = containerPort;

                if (!string.IsNullOrEmpty(middleware))
                {
                    labels[$"traefik.http.routers.{serviceName}.middlewares"] = middleware;
                }
            }

            return labels;
        }

        private async Task ConnectToTraefikNetworkAsync(string containerId, CancellationToken cancellationToken)
        {
            try
            {
                var network = await _dockerClient.Networks.InspectNetworkAsync(_traefikNetwork, cancellationToken);

                if (network.Containers?.ContainsKey(containerId) == true)
                {
                    _logger.LogInformation("Container already connected to {Network}", _traefikNetwork);
                    return;
                }

                await _dockerClient.Networks.ConnectNetworkAsync(
                    _traefikNetwork,
                    new NetworkConnectParameters { Container = containerId },
                    cancellationToken
                );

                _logger.LogInformation("Connected container to {Network} network", _traefikNetwork);
            }
            catch (DockerApiException ex) when (ex.Message.Contains("already exists"))
            {
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Traefik network {Network} not found", _traefikNetwork);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to Traefik network");
            }
        }

        private void PrintAccessibleUrls(ContainerInspectResponse container)
        {
            var ports = container.NetworkSettings?.Ports;
            if (ports == null) return;

            foreach (var portMapping in ports)
            {
                if (portMapping.Value == null || !portMapping.Value.Any())
                    continue;

                var hostPort = portMapping.Value[0].HostPort;
                string url;

                if (hostPort == "80")
                {
                    url = $"http://{_userId}.{_domain}";
                }
                else
                {
                    url = $"http://{_userId}.{_domain}/port{hostPort}";
                }

                _logger.LogInformation("  → Available at: {Url}", url);
            }
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
            });

            var logger = loggerFactory.CreateLogger<AutoLabelerService>();
            var service = new AutoLabelerService(logger);

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await service.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Shutting down...");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fatal error in Auto-Labeler service");
            }
        }
    }
}

// Required NuGet packages:
// - Docker.DotNet (>= 3.125.15)
// - Microsoft.Extensions.Logging
// - Microsoft.Extensions.Logging.Console