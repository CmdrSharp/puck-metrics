using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace PuckMetrics
{
    public class MetricsHttpServer : IDisposable
    {
        private readonly MetricRegistry _registry;
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private volatile bool _running;

        public MetricsHttpServer(MetricRegistry registry, string bindAddress, int port)
        {
            _registry = registry;
            var address = IPAddress.Parse(bindAddress);
            _listener = new TcpListener(address, port);

            _thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "PuckMetrics_HTTP"
            };
        }

        public void Start()
        {
            _running = true;
            _listener.Start();
            _thread.Start();

            Debug.Log($"[PuckMetrics] HTTP server listening on {_listener.LocalEndpoint}");
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }

            if (_thread.IsAlive)
                _thread.Join(2000);
        }

        private void ListenLoop()
        {
            while (_running)
            {
                TcpClient client = null;

                try
                {
                    client = _listener.AcceptTcpClient();
                    client.ReceiveTimeout = 2000;
                    client.SendTimeout = 2000;
                    HandleClient(client);
                }
                catch (SocketException) when (!_running) {}
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PuckMetrics] HTTP error: {ex.Message}");
                }
                finally
                {
                    client?.Close();
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII))
            {
                // Read the request line
                var requestLine = reader.ReadLine();
                if (requestLine == null) return;

                // Consume remaining headers
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }

                // Only serve GET /metrics
                if (requestLine.StartsWith("GET /metrics", StringComparison.OrdinalIgnoreCase))
                {
                    var body = _registry.Expose();
                    WriteResponse(stream, 200, "text/plain; version=0.0.4; charset=utf-8", body);
                }
                else if (requestLine.StartsWith("GET /health", StringComparison.OrdinalIgnoreCase))
                {
                    WriteResponse(stream, 200, "text/plain", "ok\n");
                }
                else
                {
                    WriteResponse(stream, 404, "text/plain", "Not Found\n");
                }
            }
        }

        private static void WriteResponse(NetworkStream stream, int statusCode, string contentType, string body)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var statusText = statusCode == 200 ? "OK" : statusCode == 404 ? "Not Found" : "Error";
            var header = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);

            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);

            stream.Flush();
        }
    }
}
