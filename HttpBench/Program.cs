﻿using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HttpBench
{
    public class Program
    {
        const string ListenHost = "localhost";
        const int ListenPort = 54321;

        readonly HttpClient _client;
        readonly IWebHost _webHost;

        [Benchmark]
        public async Task GetSimple()
        {
            using HttpRequestMessage req = new HttpRequestMessage();

            req.Method = HttpMethod.Post;
            req.RequestUri = new Uri("/", UriKind.Relative);
            req.Version = HttpVersion.Version20;
            req.Content = new StringContent("asdf", Encoding.ASCII, "text/plain");
            req.Headers.Add("x-test-foo", "some huffman-coded value");

            using HttpResponseMessage res = await _client.SendAsync(req).ConfigureAwait(false);
        }

        public Program()
        {
            var listenerFactory = new InMemoryListenerFactory();
            _client = CreateHttpClient(listenerFactory);
            _webHost = CreateWebHost(listenerFactory);
            _webHost.Start();
        }

        static HttpClient CreateHttpClient(InMemoryListenerFactory listenerFactory)
        {
            Func<string, int, CancellationToken, ValueTask<Stream>> dialer = async (host, port, cancellationToken) =>
            {
                return await listenerFactory.ConnectClientAsync(host, port, cancellationToken).ConfigureAwait(false);
            };

            var handler = new SocketsHttpHandler();
            handler.GetType().GetProperty("ConnectCallback").SetValue(handler, dialer);
            handler.SslOptions.RemoteCertificateValidationCallback = delegate { return true; };

            return new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://{ListenHost}:{ListenPort}/")
            };
        }

        static IWebHost CreateWebHost(InMemoryListenerFactory listenerFactory)
        {
            return
                WebHost.CreateDefaultBuilder()
                .UseSetting("preventHostingStartup", "true")
                .UseKestrel(ko =>
                {
                    ko.Listen(IPAddress.Loopback, ListenPort, listenOptions =>
                    {
                        listenOptions.UseHttps(CreateSelfSignedCert());
                    });
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IConnectionListenerFactory>(listenerFactory);
                })
                .ConfigureLogging(logging =>
                {
                    logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(routes =>
                    {
                        routes.MapPost("/", async ctx =>
                        {
                            await ctx.Response.WriteAsync("ok").ConfigureAwait(false);
                        });
                    });
                })
                .Build();
        }

        static X509Certificate2 CreateSelfSignedCert()
        {
            // Create self-signed cert for server.
            using RSA rsa = RSA.Create();
            var certReq = new CertificateRequest($"CN={ListenHost}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            certReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            X509Certificate2 cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
            }
            return cert;
        }

        public static void Main(string[] args)
        {
            Console.WriteLine(typeof(System.Net.Http.SocketsHttpHandler).Assembly.Location);

            Program p = new Program();

            //p.GetSimple().GetAwaiter().GetResult();
            PoorMansBenchmark(() => p.GetSimple().Wait());
            //BenchmarkRunner.Run<Program>();
        }

        static void PoorMansBenchmark(Action action)
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            Stopwatch sw = Stopwatch.StartNew();

            // Warmup.
            do
            {
                action();
            }
            while (sw.ElapsedMilliseconds < 1000);

            // Get number of loops to execute, targeting ~1s of execution for each run.
            int loops = 0;
            do
            {
                ++loops;
                action();
            }
            while (sw.ElapsedMilliseconds < 2000);

            Console.WriteLine($"Using {loops:N0} loops");

            // Keep running until we get 10 runs with no observed speed improvement.
            // Prints + when an improvement is seen, otherwise -
            long bestTicks = long.MaxValue;
            int runCount = 0;

            while (runCount < 10)
            {
                long start = Stopwatch.GetTimestamp();

                for (int i = 0; i < loops; ++i)
                {
                    action();
                }

                long end = Stopwatch.GetTimestamp();
                long curTicks = end - start;

                if (curTicks < bestTicks)
                {
                    bestTicks = curTicks;
                    runCount = 0;
                    Console.Write('+');
                }
                else
                {
                    ++runCount;
                    Console.Write('-');
                }
            }

            // Report out our best time.
            double opsPerSecond = (loops * Stopwatch.Frequency) / (double)bestTicks;
            double opTime = 1.0 / opsPerSecond;
            string timeUnit = "s";

            if (opTime < 1.0)
            {
                opTime *= 1000.0;
                timeUnit = "ms";

                if (opTime < 10)
                {
                    opTime *= 1000.0;
                    timeUnit = "us";
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Speed: {opTime:N1} {timeUnit} ({opsPerSecond:N1} reqs/s)");
        }
    }
}
