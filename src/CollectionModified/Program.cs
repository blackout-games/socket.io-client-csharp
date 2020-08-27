using SocketIOClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CollectionModified
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var uri = new Uri("http://localhost:11000/nsp");

            var socket = new SocketIO(uri, new SocketIOOptions
            {
                Query = new Dictionary<string, string>
                {
                    {"token", "io" }
                },
                ConnectionTimeout = TimeSpan.FromSeconds(10)
            });

            socket.OnConnected += Socket_OnConnected;
            socket.OnDisconnected += Socket_OnDisconnected;

            int count = 0;
            socket.On("bytes", response =>
            {
                Console.Write(++count);
                Console.WriteLine(response);
            });

            int count2 = 0;
            socket.On("hi", response =>
            {
                Console.Write(++count2);
                Console.WriteLine(response);
            });
            await socket.ConnectAsync();
            Console.ReadLine();
        }

        private static void Socket_OnDisconnected(object sender, string e)
        {
            Console.Error.WriteLine("Disconnected");
        }

        private static async void Socket_OnConnected(object sender, EventArgs e)
        {
            var socket = sender as SocketIO;
            string data = File.ReadAllText("data.txt");
            byte[] buffer = Encoding.UTF8.GetBytes(data);
            //for (int i = 0; i < 20; i++)
            //{
            //    await socket.EmitAsync("bytes", "c#", new
            //    {
            //        source = "client007",
            //        bytes = buffer
            //    });
            //}
            await Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    await socket.EmitAsync("bytes", "c#", new
                    {
                        source = "client007",
                        bytes = buffer
                    });
                    await Task.Delay(200);
                }
            });

            int hiCount = 0;
            while (true)
            {
                hiCount++;
                await socket.EmitAsync("hi", hiCount);
                await Task.Delay(200);
            }
        }
    }
}
