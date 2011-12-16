﻿/*
copyright (C) 2011 by high828@gmail.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

//#define FILETESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Remoting.Contexts;
using System.Text;
using FluorineFx;
using FluorineFx.Configuration;
using FluorineFx.Messaging.Messages;
using FluorineFx.Messaging.Rtmp;
using FluorineFx.Messaging.Rtmp.Event;
using FluorineFx.Util;
using LoLNotes.Messaging.Messages;
using NotMissing.Logging;

namespace LoLNotes.Proxy
{
	public class RtmpsProxyClient : ProxyClient
	{
		const bool encode = false;
		const bool logtofiles = false;

		protected RtmpContext sourcecontext = new RtmpContext(RtmpMode.Server) { ObjectEncoding = ObjectEncoding.AMF0 };
		protected RtmpContext remotecontext = new RtmpContext(RtmpMode.Client) { ObjectEncoding = ObjectEncoding.AMF0 };
		readonly ByteBuffer sendbuffer = new ByteBuffer(new MemoryStream());
		readonly ByteBuffer receivebuffer = new ByteBuffer(new MemoryStream());
		readonly List<Notify> InvokeList = new List<Notify>();

		public new RtmpsProxyHost Host { get; protected set; }

		protected ByteBuffer postbuffer = new ByteBuffer(new MemoryStream());


		public RtmpsProxyClient(IProxyHost host, TcpClient src)
			: base(host, src)
		{
			if (!(host is RtmpsProxyHost))
				throw new ArgumentException("Expected RtmpsProxyHost, got " + host.GetType());

			Host = (RtmpsProxyHost)host;
		}
		/// <summary>
		/// Only for test driven.
		/// </summary>
		RtmpsProxyClient()
			: base(null, null)
		{
		}

		protected override void OnSend(byte[] buffer, int idx, int len)
		{
			if (postbuffer != null)
			{
				postbuffer.Append(buffer, idx, len);
				if (postbuffer.Length > 4)
				{
					int num = 0;
					postbuffer.Dispose();
					postbuffer = null;
					if (num == 0x504f5354)
					{
						StaticLogger.Trace(string.Format("Rejecting POST request", len));
						Stop();
						return;
					}
				}
			}

			StaticLogger.Trace(string.Format("Send {0} bytes", len));

			if (logtofiles)
			{
				using (var fs = File.Open("realsend.dmp", FileMode.Append, FileAccess.Write))
				{
					fs.Write(buffer, idx, len);
				}
			}

			sendbuffer.Append(buffer, idx, len);

			var objs = RtmpProtocolDecoder.DecodeBuffer(sourcecontext, sendbuffer);
			if (objs != null)
			{
				foreach (var obj in objs)
				{
					var pck = obj as RtmpPacket;
					if (pck != null)
					{
						var inv = pck.Message as Notify;
						if (inv != null)
						{
							lock (InvokeList)
							{
								InvokeList.Add(inv);
							}
							StaticLogger.Trace(
								string.Format("Call {0}({1}) (Id:{2})",
									inv.ServiceCall.ServiceMethodName,
									string.Join(", ", inv.ServiceCall.Arguments.Select(o => o.ToString())),
									pck.Header.ChannelId
								)
							);
						}
						else
						{
							StaticLogger.Trace(string.Format("Sent {0} (Id:{1})", pck.Message.GetType(), pck.Header.ChannelId));
						}
					}

					if (obj != null && encode)
					{
						var buf = RtmpProtocolEncoder.Encode(sourcecontext, obj);
						if (pck != null && pck.Message is Notify)
						{
							sourcecontext.ObjectEncoding = ObjectEncoding.AMF3;
						}
						if (buf == null)
						{
							StaticLogger.Fatal("Unable to encode " + obj);
						}
						else
						{
							var buff = buf.ToArray();
							if (logtofiles)
							{
								using (var fs = File.Open("send.dmp", FileMode.Append, FileAccess.Write))
								{
									fs.Write(buff, idx, buff.Length);
								}
							}
							if (encode)
								base.OnSend(buff, idx, buff.Length);
						}
					}
				}
			}

			if (!encode)
				base.OnSend(buffer, idx, len);
		}

		protected override void OnReceive(byte[] buffer, int idx, int len)
		{
			StaticLogger.Trace(string.Format("Recv {0} bytes", len));

			if (logtofiles)
			{
				using (var fs = File.Open("realrecv.dmp", FileMode.Append, FileAccess.Write))
				{
					fs.Write(buffer, idx, len);
				}
			}

			receivebuffer.Append(buffer, idx, len);

			var objs = RtmpProtocolDecoder.DecodeBuffer(remotecontext, receivebuffer);
			if (objs != null)
			{

				foreach (var obj in objs)
				{
					var pck = obj as RtmpPacket;
					if (pck != null)
					{
						var result = pck.Message as Notify;
						if (result != null)
						{
							Notify inv = null;
							if (result.ServiceCall.ServiceMethodName == "_result" || result.ServiceCall.ServiceMethodName == "_error")
							{
								lock (InvokeList)
								{
									int fidx = InvokeList.FindIndex(i => i.InvokeId == result.InvokeId);
									if (fidx == -1)
									{
										StaticLogger.Warning(string.Format("Call not found for {0} (Id:{1})", result.InvokeId, pck.Header.ChannelId));
									}
									else
									{
										inv = InvokeList[fidx];
										InvokeList.RemoveAt(fidx);
									}
								}

								if (inv != null)
								{
									OnCall(inv, result);

									StaticLogger.Trace(
										string.Format(
											"Ret  ({0}) (Id:{1})",
											string.Join(", ", inv.ServiceCall.Arguments.Select(o => o.ToString())),
											pck.Header.ChannelId
										)
									);
								}
							}

							//Call was not found. Most likely a receive message.
							if (inv == null)
							{
								OnNotify(result);
							}
						}
						else
						{
							StaticLogger.Trace(string.Format("Recv {0} (Id:{1})", pck.Message, pck.Header.ChannelId));
						}
					}

					if (obj != null && encode)
					{
						var buf = RtmpProtocolEncoder.Encode(remotecontext, obj);
						if (pck != null && pck.Message is Notify)
						{
							remotecontext.ObjectEncoding = ObjectEncoding.AMF3;
						}
						if (buf == null)
						{
							StaticLogger.Fatal("Unable to encode " + obj);
						}
						else
						{
							var buff = buf.ToArray();
							if (logtofiles)
							{
								using (var fs = File.Open("recv.dmp", FileMode.Append, FileAccess.Write))
								{
									fs.Write(buff, idx, buff.Length);
								}
							}
							if (encode)
								base.OnReceive(buff, idx, buff.Length);
						}
					}
				}
			}

			if (!encode)
				base.OnReceive(buffer, idx, len);

		}

		protected virtual void OnCall(Notify call, Notify result)
		{
			Host.OnCall(this, call, result);
		}
		protected virtual void OnNotify(Notify notify)
		{
			Host.OnNotify(this, notify);
		}
	}
}
