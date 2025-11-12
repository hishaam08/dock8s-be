using Microsoft.AspNetCore.SignalR;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Text;
using System.Collections.Concurrent;

namespace Dock8s.Application.SignalRHub
{
    public class TerminalHub : Hub
    {
        private readonly DockerClient _dockerClient;
        private static readonly ConcurrentDictionary<string, MultiplexedStream> _streams = new();
        private static readonly ConcurrentDictionary<string, string> _execIds = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();

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
                // Create exec instance with bash (fallback to sh if bash not available)
                var exec = await _dockerClient.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
                {
                    AttachStdout = true,
                    AttachStderr = true,
                    AttachStdin = true,
                    Tty = true,
                    Cmd = new[] { "/bin/sh" },
                    Env = new[]
                    {
                        "TERM=xterm-256color",
                        "PS1=\\u@\\h:\\w\\$ " // Set a clear prompt
                    }
                });

                var cts = new CancellationTokenSource();
                _cancellationTokens[Context.ConnectionId] = cts;

                var stream = await _dockerClient.Exec.StartAndAttachContainerExecAsync(exec.ID, false, cts.Token);
                _streams[Context.ConnectionId] = stream;
                _execIds[Context.ConnectionId] = exec.ID;

                Console.WriteLine($"[ATTACH] Connected to container {containerId} with exec {exec.ID}");

                // Capture the IClientProxy before leaving the hub method
                var caller = Clients.Caller;
                var connectionId = Context.ConnectionId;

                // Send initial ready signal
                await caller.SendAsync("ReceiveOutput", "");

                _ = Task.Run(async () =>
                {
                    var buffer = new byte[4096]; // Increased buffer size for better performance

                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);
                            
                            if (result.EOF)
                            {
                                Console.WriteLine($"[EOF] Stream ended for {connectionId}");
                                break;
                            }

                            if (result.Count > 0)
                            {
                                var output = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                
                                // Log output for debugging (can be removed in production)
                                Console.WriteLine($"[OUTPUT <- DOCKER] {result.Count} bytes");
                                
                                await caller.SendAsync("ReceiveOutput", output);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"[CANCELLED] Read operation cancelled for {connectionId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[READ ERROR] {ex.GetType().Name}: {ex.Message}");
                        await caller.SendAsync("ReceiveOutput", $"\r\n[Connection Error: {ex.Message}]\r\n");
                    }
                    finally
                    {
                        // Cleanup on stream end
                        if (_streams.TryRemove(connectionId, out var s))
                        {
                            s?.Dispose();
                        }
                        _execIds.TryRemove(connectionId, out _);
                        _cancellationTokens.TryRemove(connectionId, out _);
                    }
                }, cts.Token);

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ATTACH ERROR] {ex.GetType().Name}: {ex.Message}");
                await Clients.Caller.SendAsync("ReceiveOutput", $"Error attaching to container: {ex.Message}\r\n");
            }
        }

        public async Task ResizeTerminal(int cols, int rows)
        {
            if (_execIds.TryGetValue(Context.ConnectionId, out var execId))
            {
                try
                {
                    await _dockerClient.Exec.ResizeContainerExecTtyAsync(execId, new ContainerResizeParameters
                    {
                        Height = rows,
                        Width = cols
                    });
                    Console.WriteLine($"[RESIZE] {cols}x{rows} for exec {execId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RESIZE ERROR] {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[RESIZE] No exec ID found for {Context.ConnectionId}");
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
                    
                    // Log special characters for debugging
                    var logData = data.Replace("\r", "\\r")
                                     .Replace("\n", "\\n")
                                     .Replace("\t", "\\t")
                                     .Replace("\x1b", "\\x1b");
                    Console.WriteLine($"[INPUT -> DOCKER] {bytes.Length} bytes: {logData}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WRITE ERROR] {ex.GetType().Name}: {ex.Message}");
                    await Clients.Caller.SendAsync("ReceiveOutput", $"\r\n[Write Error: {ex.Message}]\r\n");
                }
            }
            else
            {
                Console.WriteLine($"[NO STREAM] No stream found for {Context.ConnectionId}");
            }
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var connectionId = Context.ConnectionId;
            Console.WriteLine($"[DISCONNECT] Connection {connectionId} disconnected");

            // Cancel any ongoing operations
            if (_cancellationTokens.TryRemove(connectionId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            // Clean up stream
            if (_streams.TryRemove(connectionId, out var stream))
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception disposeEx)
                {
                    Console.WriteLine($"[DISPOSE ERROR] {disposeEx.Message}");
                }
            }

            // Clean up exec ID
            _execIds.TryRemove(connectionId, out _);

            if (ex != null)
            {
                Console.WriteLine($"[DISCONNECT ERROR] {ex.GetType().Name}: {ex.Message}");
            }

            await base.OnDisconnectedAsync(ex);
        }

        // Optional: Method to check if connection is alive
        public async Task<bool> Ping()
        {
            return await Task.FromResult(_streams.ContainsKey(Context.ConnectionId));
        }

        // Optional: Method to forcefully terminate exec session
        public async Task Terminate()
        {
            if (_execIds.TryGetValue(Context.ConnectionId, out var execId))
            {
                try
                {
                    // Send Ctrl+C to terminate current process
                    await SendInput("\x03");
                    Console.WriteLine($"[TERMINATE] Sent SIGINT to exec {execId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TERMINATE ERROR] {ex.Message}");
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dockerClient?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}