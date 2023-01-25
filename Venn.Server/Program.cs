﻿using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using SimpleInjector;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Venn.Data;
using Venn.Data.Repos;
using Venn.Models.Models.Concretes;

namespace Venn.Server
{
    class Program
    {
        static int maxValue = ushort.MaxValue - 28;

        static Container container;

        static List<Client> clients;

        static TcpListener listener;

        static IRepository<User> userRepo;

        static IRepository<Message> messageRepo;

        static IRepository<Notification> notificationRepo;

        static IRepository<Friendship> friendshipRepo;

        private static Dispatcher dispatcher = Dispatcher.CreateDefault();

        static void Main(string[] args)
        {
            container = new Container();
            container.RegisterSingleton<VennDbContext>();
            container.RegisterSingleton<IRepository<User>, Repository<User>>();
            container.RegisterSingleton<IRepository<Message>, Repository<Message>>();
            container.RegisterSingleton<IRepository<Notification>, Repository<Notification>>();
            container.RegisterSingleton<IRepository<Friendship>, Repository<Friendship>>();
            userRepo = container.GetInstance<IRepository<User>>();
            messageRepo = container.GetInstance<IRepository<Message>>();
            notificationRepo = container.GetInstance<IRepository<Notification>>();
            friendshipRepo = container.GetInstance<IRepository<Friendship>>();
            clients = new List<Client>();
            listener = new TcpListener(IPAddress.Parse("192.168.56.1"), 27001);

            JsonSerializerOptions options = new()
            {
                ReferenceHandler = ReferenceHandler.IgnoreCycles,
                WriteIndented = true
            };

             listener.Start();
            Console.WriteLine($"[{DateTime.Now}]: Listener has started");

            while (true)
            {
                var newClient = new Client(listener.AcceptTcpClient());
                var ip = newClient.TcpClient.Client.RemoteEndPoint.ToString().Split(':')[0];
                Console.WriteLine($"[{ip}]: Client has connected");
                Task.Run(() =>
                {
                    var client = new Client(newClient.TcpClient);
                    clients.Add(client);
                    while (client.TcpClient.Connected)
                    {
                        string str;
                        try
                        {
                            var ns = client.TcpClient.GetStream();
                            var bytes = new byte[maxValue];
                            var length = ns.Read(bytes, 0, bytes.Length);
                            str = Encoding.Default.GetString(bytes, 0, length);
                        }
                        catch (Exception)
                        {
                            Console.WriteLine($"[{ip}]: Client has disconnected");
                            clients.Remove(client);
                            break;
                        }

                        var command = str.Split('$')[0];
                        if (command == "login")
                        {
                            var email = str.Split('$')[1];
                            var password = str.Split('$')[2];
                            var user = userRepo.GetAll().FirstOrDefault(u => u.Email == email);
                            if (user != null)
                            {
                                if (user.Password == password)
                                {
                                    
                                    
                                    client.User = user;
                                    Console.WriteLine($"[{ip}]: Client has logined");
                                }
                                else
                                {
                                    var r = "password$Sorry, your password was incorrect.";
                                    client.TcpClient.Client.Send(Encoding.UTF8.GetBytes(r));
                                    Console.WriteLine($"[{ip}]: Client login failed");
                                }
                            }
                            else
                            {
                                var r = "email$The email you entered isn’t connected to an account";
                                client.TcpClient.Client.Send(Encoding.UTF8.GetBytes(r));
                                Console.WriteLine($"[{ip}]: Client login failed");
                            }
                        }
                        else if (command == "create")
                        {
                            bool b = true;
                            var user = JsonSerializer.Deserialize<User>(str.Split('$')[1]);
                            foreach (var u in userRepo.GetAll())
                            {
                                if (u.Email == user.Email)
                                {
                                    b = false;
                                    break;
                                }
                            }
                            if (b)
                            {
                                lock (new object())
                                {
                                    userRepo.Add(user);
                                    userRepo.SaveChanges();
                                }
                                client.TcpClient.Client.Send(Encoding.UTF8.GetBytes("true"));
                                Console.WriteLine($"[{ip}]: Client has created user");
                            }
                            else
                            {
                                client.TcpClient.Client.Send(Encoding.UTF8.GetBytes("false"));
                                Console.WriteLine($"[{ip}]: Client create user failed");
                            }
                        }
                        else if (command == "message")
                        {
                            var message = JsonSerializer.Deserialize<Message>(str.Split('$')[1]);
                            client.User.Messages.Add(message);
                            messageRepo.Add(message);
                            messageRepo.SaveChanges();
                        }
                        else if (command == "users")
                        {
                            var friend = str.Split('$')[1];
                            var userList = new List<User>();
                            foreach (var u in userRepo.GetAll().Include("Contacts"))
                            {
                                if ((u.Username.Contains(friend) || u.ToString().Contains(friend)) && u.Id != client.User.Id)
                                {
                                    userList.Add(u);
                                }
                            }

                            var r = $"users${JsonSerializer.Serialize(userList, options)}";

                            if (Encoding.UTF8.GetBytes(r).Length > maxValue)
                            {
                                r = $"<{r}>";
                                var data = Encoding.UTF8.GetBytes(r);
                                var skipCount = 0;
                                var bytesLen = data.Length;

                                while (skipCount + maxValue <= bytesLen)
                                {
                                    client.TcpClient.Client.Send(data
                                        .Skip(skipCount)
                                        .Take(maxValue)
                                        .ToArray());
                                    skipCount += maxValue;
                                }

                                if (skipCount != bytesLen)
                                    client.TcpClient.Client.Send(data
                                        .Skip(skipCount)
                                        .Take(bytesLen - skipCount)
                                        .ToArray());
                            }
                            else
                            {
                                client.TcpClient.Client.Send(Encoding.UTF8.GetBytes(r));
                            }
                        }
                        else if (command == "noti")
                        {
                            var noti = JsonSerializer.Deserialize<Notification>(str.Split('$')[1]);
                            client.User.Notifications.Add(noti);
                            notificationRepo.Add(noti);
                            notificationRepo.SaveChanges();

                            foreach (var c in clients)
                            {
                                if (c.User.Id == noti.ToUserId)
                                {
                                    var r = $"noti${JsonSerializer.Serialize(noti, options)}";

                                    if (Encoding.UTF8.GetBytes(r).Length > maxValue)
                                    {
                                        r = $"<{r}>";
                                        var data = Encoding.UTF8.GetBytes(r);
                                        var skipCount = 0;
                                        var bytesLen = data.Length;

                                        while (skipCount + maxValue <= bytesLen)
                                        {
                                            c.TcpClient.Client.Send(data
                                                .Skip(skipCount)
                                                .Take(maxValue)
                                                .ToArray());
                                            skipCount += maxValue;
                                        }

                                        if (skipCount != bytesLen)
                                            c.TcpClient.Client.Send(data
                                                .Skip(skipCount)
                                                .Take(bytesLen - skipCount)
                                                .ToArray());
                                    }
                                    else
                                    {
                                        c.TcpClient.Client.Send(Encoding.UTF8.GetBytes(r));
                                    }
                                    break;
                                }
                            }
                        }
                    }
                });
            }
        }
    }
}