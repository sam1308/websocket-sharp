#region MIT License
/**
 * WebSocket.cs
 *
 * A C# implementation of a WebSocket protocol client.
 * This code derived from WebSocket.java (http://github.com/adamac/Java-WebSocket-client).
 *
 * The MIT License
 *
 * Copyright (c) 2009 Adam MacBeth
 * Copyright (c) 2010 sta.blockhead
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion

#if NOTIFY
using Notifications;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WebSocketSharp
{
  public delegate void MessageEventHandler(object sender, string eventdata);

  public class WebSocket : IDisposable
  {
    private Uri uri;
    public string Url
    {
      get
      {
        return uri.ToString();
      }
    }

    private volatile WsState readyState;
    public WsState ReadyState
    {
      get
      {
        return readyState;
      }

      private set
      {
        switch (value)
        {
          case WsState.OPEN:
            if (OnOpen != null)
            {
              OnOpen(this, EventArgs.Empty);
            }
            goto default;
          case WsState.CLOSING:
          case WsState.CLOSED:
            close(value);
            break;
          default:
            readyState = value;
            break;
        }
      }
    }

    private StringBuilder unTransmittedBuffer;
    public String UnTransmittedBuffer
    {
      get { return unTransmittedBuffer.ToString(); }
    }

    private long bufferedAmount;
    public long BufferedAmount
    {
      get { return bufferedAmount; }
    }

    private string protocol;
    public string Protocol
    {
      get { return protocol; }
    }

    private TcpClient tcpClient;
    private NetworkStream netStream;
    private SslStream sslStream;
    private IWsStream wsStream;
    private Thread msgThread;
#if NOTIFY
    private Notification msgNf;
    public Notification MsgNf
    {
      get { return msgNf; }
    }
#endif
    public event EventHandler OnOpen;
    public event MessageEventHandler OnMessage;
    public event MessageEventHandler OnError;
    public event EventHandler OnClose;

    public WebSocket(string url)
      : this(url, String.Empty)
    {
    }

    public WebSocket(string url, string protocol)
    {
      this.uri = new Uri(url);
      string scheme = uri.Scheme;

      if (scheme != "ws" && scheme != "wss")
      {
        throw new ArgumentException("Unsupported scheme: " + scheme);
      }

      this.readyState = WsState.CONNECTING;
      this.unTransmittedBuffer = new StringBuilder();
      this.bufferedAmount = 0;
      this.protocol = protocol;
    }

    public void Connect()
    {
      createConnection();
      doHandshake();

      this.msgThread = new Thread(new ThreadStart(message)); 
      msgThread.IsBackground = true;
      msgThread.Start();
    }

    public void Send(string data)
    {
      if (readyState == WsState.CONNECTING)
      {
        throw new InvalidOperationException("Handshake not complete.");
      }

      byte[] dataBuffer = Encoding.UTF8.GetBytes(data);

      try
      {
        wsStream.WriteByte(0x00);
        wsStream.Write(dataBuffer, 0, dataBuffer.Length);
        wsStream.WriteByte(0xff);
      }
      catch (Exception e)
      {
        unTransmittedBuffer.Append(data);
        bufferedAmount += dataBuffer.Length;

        if (OnError != null)
        {
          OnError(this, e.Message);
        }
#if DEBUG
        Console.WriteLine("WS: Error @Send: {0}", e.Message);
#endif
      }
    }

    public void Close()
    {
      ReadyState = WsState.CLOSING;
    }

    public void Dispose()
    {
      Close();
    }

    private void close(WsState state)
    {
#if DEBUG
      Console.WriteLine("WS: Info @close: Current thread IsBackground: {0}", Thread.CurrentThread.IsBackground);
#endif
      if (readyState == WsState.CLOSING ||
          readyState == WsState.CLOSED)
      {
        return;
      }

      readyState = state;

      if (OnClose != null)
      {
        OnClose(this, EventArgs.Empty);
      }

      if (wsStream != null && tcpClient.Connected)
      {
        try
        {
          wsStream.WriteByte(0xff);
          wsStream.WriteByte(0x00);
        }
        catch (Exception e)
        {
#if DEBUG
          Console.WriteLine("WS: Error @close: {0}", e.Message);
#endif
        }
      }

      if (!(Thread.CurrentThread.IsBackground) &&
          msgThread != null && msgThread.IsAlive)
      {
        msgThread.Join();
      }
       
      if (wsStream != null)
      {
        wsStream.Dispose();
        wsStream = null;
      }

      if (tcpClient != null)
      {
        tcpClient.Close();
        tcpClient = null;
      }
    }

    private void createConnection()
    {
      string scheme = uri.Scheme;
      string host = uri.DnsSafeHost;
      int port = uri.Port;

      if (port <= 0)
      {
        if (scheme == "wss")
        {
          port = 443;
        }
        else
        {
          port = 80;
        }
      }

      this.tcpClient = new TcpClient(host, port);
      this.netStream = tcpClient.GetStream();

      if (scheme == "wss")
      {
        this.sslStream = new SslStream(netStream);
        sslStream.AuthenticateAsClient(host);
        this.wsStream = new WsStream<SslStream>(sslStream);
      }
      else
      {
        this.wsStream = new WsStream<NetworkStream>(netStream);
      }
    }

    private void doHandshake()
    {
      string path = uri.PathAndQuery;
      string host = uri.DnsSafeHost;
      string origin = "http://" + host;

      int port = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Port;
      if (port != 80)
      {
        host += ":" + port;
      }

      string subprotocol = protocol != String.Empty
                           ? String.Format("Sec-WebSocket-Protocol: {0}\r\n", protocol)
                           : protocol;

      string request = "GET " + path + " HTTP/1.1\r\n" +
                       "Upgrade: WebSocket\r\n" +
                       "Connection: Upgrade\r\n" +
                       subprotocol +
                       "Host: " + host + "\r\n" +
                       "Origin: " + origin + "\r\n" +
                       "\r\n";
#if DEBUG
      Console.WriteLine("WS: Info @doHandshake: Handshake from client: \n{0}", request);
#endif
      byte[] sendBuffer = Encoding.UTF8.GetBytes(request);
      wsStream.Write(sendBuffer, 0, sendBuffer.Length);

      string[] response;
      List<byte> rawdata = new List<byte>();

      while (true)
      {
        if (wsStream.ReadByte().EqualsWithSaveTo('\r', rawdata) &&
            wsStream.ReadByte().EqualsWithSaveTo('\n', rawdata) &&
            wsStream.ReadByte().EqualsWithSaveTo('\r', rawdata) &&
            wsStream.ReadByte().EqualsWithSaveTo('\n', rawdata))
        {
          break;
        }
      }

      response = Encoding.UTF8.GetString(rawdata.ToArray())
        .Replace("\r\n", "\n").Replace("\n\n", "\n")
        .Split('\n');
#if DEBUG
      Console.WriteLine("WS: Info @doHandshake: Handshake from server:");
      foreach (string s in response)
      {
        Console.WriteLine("{0}", s);
      }
#endif
      Action<string> action = s => { throw new IOException("Invalid handshake response: " + s); };
      response[0].NotEqualsDo("HTTP/1.1 101 Web Socket Protocol Handshake", action);
      response[1].NotEqualsDo("Upgrade: WebSocket", action);
      response[2].NotEqualsDo("Connection: Upgrade", action);

      for (int i = 3; i < response.Length; i++)
      {
        if (response[i].Contains("WebSocket-Protocol:"))
//        if (response[i].Contains("Sec-WebSocket-Protocol:"))
        {
          int j = response[i].IndexOf(":");
          protocol = response[i].Substring(j + 1).Trim();
        }
      }
#if DEBUG
      Console.WriteLine("WS: Info @doHandshake: Sub protocol: {0}", protocol);
#endif

      ReadyState = WsState.OPEN;
    }

    private void message()
    {
#if DEBUG
      Console.WriteLine("WS: Info @message: Current thread IsBackground: {0}", Thread.CurrentThread.IsBackground);
#endif
      string data;
#if NOTIFY
      this.msgNf = new Notification();
      msgNf.AddHint("append", "allowed");
#endif
      while (readyState == WsState.OPEN)
      {
        while (readyState == WsState.OPEN && netStream.DataAvailable)
        {
          data = receive();

          if (OnMessage != null && data != null)
          {
            OnMessage(this, data);
          }
        }
      }
#if DEBUG
      Console.WriteLine("WS: Info @message: Exit message method.");
#endif
    }

    private string receive()
    {
      try
      {
        byte frame_type = (byte)wsStream.ReadByte();
        byte b;

        if ((frame_type & 0x80) == 0x80)
        {
          // Skip data frame
          int len = 0;
          int b_v;

          do
          {
            b = (byte)wsStream.ReadByte();
            b_v = b & 0x7f;
            len = len * 128 + b_v;
          }
          while ((b & 0x80) == 0x80);

          for (int i = 0; i < len; i++)
          {
            wsStream.ReadByte();
          }

          if (frame_type == 0xff && len == 0)
          {
            ReadyState = WsState.CLOSED;
#if DEBUG
            Console.WriteLine("WS: Info @receive: Server start closing handshake.");
#endif
          }
        }
        else if (frame_type == 0x00)
        {
          List<byte> raw_data = new List<byte>();

          while (true)
          {
            b = (byte)wsStream.ReadByte();

            if (b == 0xff)
            {
              break;
            }

            raw_data.Add(b);
          }

          return Encoding.UTF8.GetString(raw_data.ToArray());
        }
      }
      catch (Exception e)
      {
        if (OnError != null)
        {
          OnError(this, e.Message);
        }

        ReadyState = WsState.CLOSED;
#if DEBUG
        Console.WriteLine("WS: Error @receive: {0}", e.Message);
#endif
      }

      return null;
    }
  }
}