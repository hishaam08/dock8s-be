using Microsoft.AspNetCore.SignalR;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Text;

namespace Dock8s.Application.SignalRHub
{
    public class TerminalHub : Hub
    {
        private readonly DockerClient _dockerClient;
        private static readonly Dictionary<string, MultiplexedStream> _streams = new();

        public TerminalHub()
        {
            _dockerClient = new DockerClientConfiguration(
                new Uri("tcp://localhost:2375")
            ).CreateClient();
        }

        public async Task Attach(string containerId)
        {

            try
            {
                var exec = await _dockerClient.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
                {
                    AttachStdout = true,
                    AttachStderr = true,
                    AttachStdin = true,
                    Tty = true,
                    Cmd = new[] { "/bin/sh" }
                });

                var stream = await _dockerClient.Exec.StartAndAttachContainerExecAsync(exec.ID, false, default);
                _streams[Context.ConnectionId] = stream;

                // Capture the IClientProxy before leaving the hub method
                var caller = Clients.Caller;

                _ = Task.Run(async () =>
                {
                    var buffer = new byte[1024];

                    try
                    {
                        while (true)
                        {
                            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, default);
                            if (result.EOF)
                            {
                                break;
                            }

                            var output = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            if (!string.IsNullOrEmpty(output))
                            {
                                // ⬅️ Use captured proxy instead of this.Clients
                                await caller.SendAsync("ReceiveOutput", output);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[READ ERROR] {ex.Message}");
                    }
                });

            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveOutput", $"Error attaching to container: {ex.Message}\r\n");
            }
        }

        public async Task SendInput(string data)
        {
            if (_streams.TryGetValue(Context.ConnectionId, out var stream))
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(data);
                    await stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
                    Console.WriteLine($"📥 [INPUT -> DOCKER] Sent {bytes.Length} bytes");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WRITE ERROR] {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[NO STREAM] No stream for {Context.ConnectionId}");
            }
        }

        public override Task OnDisconnectedAsync(Exception? ex)
        {
            if (_streams.Remove(Context.ConnectionId, out var stream))
            {
                stream.Dispose();
            }

            if (ex != null)
                Console.WriteLine($"[DISCONNECT ERROR] {ex.Message}");

            return base.OnDisconnectedAsync(ex);
        }
    }
}