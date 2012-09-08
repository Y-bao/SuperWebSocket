﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SuperSocket.Common;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Command;
using SuperSocket.SocketBase.Protocol;

namespace SuperWebSocket.Protocol
{
    class WebSocketHeaderRequestFilter : WebSocketRequestFilterBase
    {
        private static readonly byte[] m_HeaderTerminator = Encoding.UTF8.GetBytes("\r\n\r\n");

        private readonly SearchMarkState<byte> m_SearchState;

        public WebSocketHeaderRequestFilter(IWebSocketSession session)
            : base(session)
        {
            m_SearchState = new SearchMarkState<byte>(m_HeaderTerminator);
        }

        public override IWebSocketFragment Filter(byte[] readBuffer, int offset, int length, bool isReusableBuffer, out int left)
        {
            left = 0;

            int prevMatched = m_SearchState.Matched;

            var result = readBuffer.SearchMark(offset, length, m_SearchState);

            if (result < 0)
            {
                this.AddArraySegment(readBuffer, offset, length, isReusableBuffer);
                return null;
            }

            int findLen = result - offset;
            string header = string.Empty;

            if (this.BufferSegments.Count > 0)
            {
                if (findLen > 0)
                {
                    this.AddArraySegment(readBuffer, offset, findLen, false);
                    header = this.BufferSegments.Decode(Encoding.UTF8);
                }
                else
                {
                    header = this.BufferSegments.Decode(Encoding.UTF8, 0, this.BufferSegments.Count - prevMatched);
                }
            }
            else
            {
                header = Encoding.UTF8.GetString(readBuffer, offset, findLen);
            }

            var webSocketSession = Session;

            try
            {
                WebSocketServer.ParseHandshake(webSocketSession, new StringReader(header));
            }
            catch (Exception e)
            {
                webSocketSession.Logger.Error("Failed to parse handshake!" + Environment.NewLine + header, e);
                webSocketSession.Close(CloseReason.ProtocolError);
                return null;
            }

            var secWebSocketKey1 = webSocketSession.Items.GetValue<string>(WebSocketConstant.SecWebSocketKey1, string.Empty);
            var secWebSocketKey2 = webSocketSession.Items.GetValue<string>(WebSocketConstant.SecWebSocketKey2, string.Empty);
            var secWebSocketVersion = webSocketSession.SecWebSocketVersion;

            left = length - findLen - (m_HeaderTerminator.Length - prevMatched);

            this.ClearBufferSegments();

            if (string.IsNullOrEmpty(secWebSocketKey1) && string.IsNullOrEmpty(secWebSocketKey2))
            {
                //draft-hixie-thewebsocketprotocol-75
                if(Handshake(webSocketSession.AppServer.WebSocketProtocolProcessor, webSocketSession))
                    return HandshakeRequestInfo;
            }
            else if ("6".Equals(secWebSocketVersion)) //draft-ietf-hybi-thewebsocketprotocol-06
            {
                if(Handshake(webSocketSession.AppServer.WebSocketProtocolProcessor, webSocketSession))
                    return HandshakeRequestInfo;
            }
            else
            {
                //draft-hixie-thewebsocketprotocol-76/draft-ietf-hybi-thewebsocketprotocol-00
                //Read SecWebSocketKey3(8 bytes)
                if (left == SecKey3Len)
                {
                    webSocketSession.Items[WebSocketConstant.SecWebSocketKey3] = readBuffer.CloneRange(offset + length - left, left);
                    left = 0;
                    if(Handshake(webSocketSession.AppServer.WebSocketProtocolProcessor, webSocketSession))
                        return HandshakeRequestInfo;
                }
                else if (left > SecKey3Len)
                {
                    webSocketSession.Items[WebSocketConstant.SecWebSocketKey3] = readBuffer.CloneRange(offset + length - left, 8);
                    left -= 8;
                    if(Handshake(webSocketSession.AppServer.WebSocketProtocolProcessor, webSocketSession))
                        return HandshakeRequestInfo;
                }
                else
                {
                    //left < 8
                    if (left > 0)
                    {
                        AddArraySegment(readBuffer, offset + length - left, left, isReusableBuffer);
                        left = 0;
                    }

                    NextRequestFilter = new WebSocketSecKey3RequestFilter(this);
                    return null;
                }
            }

            return null;
        }
    }
}
