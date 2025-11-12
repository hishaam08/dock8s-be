// using System;
// using System.IO;
// using System.Text;
// using System.Threading;
// using System.Threading.Tasks;
// using Docker.DotNet;
// using Docker.DotNet.Models;

// class Program
// {
//     static async Task Main(string[] args)
//     {
//         Console.Write("Enter container ID: ");
//         var containerId = "810e72fe5df2";

//         // Connect to Docker daemon (local UNIX socket or SSH tunnel)
//         var dockerClient = new DockerClientConfiguration(
//             new Uri("tcp://localhost:2375") // Change to unix:///var/run/docker.sock for Linux local
//         ).CreateClient();

//         // Create exec instance
//         var execCreateResponse = await dockerClient.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
//         {
//             AttachStdout = true,
//             AttachStderr = true,
//             AttachStdin = true,
//             Tty = true,
//             Cmd = new[] { "/bin/sh" } // or /bin/bash if available
//         });

//         // Start the exec session and attach
//         using var stream = await dockerClient.Exec.StartAndAttachContainerExecAsync(
//             execCreateResponse.ID,
//             false,
//             default
//         );

//         Console.WriteLine("\n✅ Connected to container shell. Type commands below:");
//         Console.WriteLine("Type 'exit-tty' to close session.\n");

//         // Create two tasks for bidirectional streaming
//         var readTask = Task.Run(async () =>
//         {
//             var buffer = new byte[1024];
//             while (true)
//             {
//                 var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
//                 if (result.EOF) break;

//                 var output = Encoding.UTF8.GetString(buffer, 0, result.Count);
//                 Console.Write(output);
//             }
//         });

//         var writeTask = Task.Run(async () =>
//         {
//             while (true)
//             {
//                 var input = Console.ReadLine();
//                 if (input == null || input.Trim().ToLower() == "exit-tty")
//                     break;

//                 var bytes = Encoding.UTF8.GetBytes(input + "\n");
//                 await stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
//                 // await stream.FlushAsync(CancellationToken.None);
//             }
//         });

//         await Task.WhenAny(readTask, writeTask);

//         Console.WriteLine("\n🔒 Session closed.");
//     }
// }

using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        // Adjust this to match your backend URL
        var hubUrl = "https://api.dock8s.in/terminalHub";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Listen for Docker output
        connection.On<string>("ReceiveOutput", (output) =>
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(output);
            Console.ResetColor();
        });

        Console.WriteLine($"Connecting to {hubUrl}...");
        await connection.StartAsync();
        Console.WriteLine("Connected to SignalR TerminalHub\n");

        // Attach to a running container
        Console.Write("Enter container ID: ");
        var containerId = "810e72fe5df2";
        await connection.InvokeAsync("Attach", containerId);

        Console.WriteLine("🔗 Attached. Type commands to send to container (type 'exit' to quit):\n");

        // Loop to send user input to container
        while (true)
        {
            var cmd = Console.ReadLine();
            if (cmd == null || cmd.ToLower() == "exit") break;

            await connection.InvokeAsync("SendInput", cmd + "\n");
        }

        Console.WriteLine("Closing connection...");
        await connection.StopAsync();
        Console.WriteLine("Connection closed.");
    }
}
