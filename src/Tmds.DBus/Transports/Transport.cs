// Copyright 2006 Alp Toker <alp@atoker.com>
// Copyright 2016 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Tmds.DBus.Transports
{
    internal class Transport : IMessageStream
    {
        private static readonly byte[] _oneByteArray = new[] { (byte)0 };
        private readonly byte[] _headerReadBuffer = new byte[16];
        private readonly List<UnixFd> _fileDescriptors = new List<UnixFd>();
        private readonly ConnectionContext _context;
        private TransportSocket _socket;

        public static async Task<IMessageStream> OpenAsync(AddressEntry entry, ConnectionContext connectionContext, CancellationToken cancellationToken)
        {
            Transport transport = new Transport(connectionContext);
            await transport.OpenAsync(entry, cancellationToken);
            return transport;
        }

        private async Task OpenAsync(AddressEntry entry, CancellationToken cancellationToken)
        {
            try
            {
                _socket = await TransportSocket.ConnectAsync(entry, cancellationToken, _context.SupportsFdPassing);
                await _socket.SendAsync(_oneByteArray, 0, 1);
                await DoSaslAuthenticationAsync(entry.Guid);
            }
            catch
            {
                _socket?.Dispose();
                throw;
            }
        }

        private Transport(ConnectionContext context)
        {
            _context = context;
        }

        public async Task<Message> ReceiveMessageAsync()
        {
            try
            {
                int bytesRead = await ReadCountAsync(_headerReadBuffer, 0, 16, _fileDescriptors);
                if (bytesRead == 0)
                    return null;
                if (bytesRead != 16)
                    throw new ProtocolException("Header read length mismatch: " + bytesRead + " of expected " + "16");

                EndianFlag endianness = (EndianFlag)_headerReadBuffer[0];
                MessageReader reader = new MessageReader(endianness, new ArraySegment<byte>(_headerReadBuffer));

                //discard endian byte, message type and flags, which we don't care about here
                reader.Seek(3);

                byte version = reader.ReadByte();
                if (version != ProtocolInformation.Version)
                    throw new NotSupportedException("Protocol version '" + version.ToString() + "' is not supported");

                uint bodyLength = reader.ReadUInt32();

                //discard _methodSerial
                reader.ReadUInt32();

                uint headerLength = reader.ReadUInt32();

                int bodyLen = (int)bodyLength;
                int toRead = (int)headerLength;

                //we fixup to include the padding following the header
                toRead = ProtocolInformation.Padded(toRead, 8);

                long msgLength = toRead + bodyLen;
                if (msgLength > ProtocolInformation.MaxMessageLength)
                    throw new ProtocolException("Message length " + msgLength + " exceeds maximum allowed " + ProtocolInformation.MaxMessageLength + " bytes");

                byte[] header = new byte[16 + toRead];
                Array.Copy(_headerReadBuffer, header, 16);
                bytesRead = await ReadCountAsync(header, 16, toRead, _fileDescriptors);
                if (bytesRead != toRead)
                    throw new ProtocolException("Message header length mismatch: " + bytesRead + " of expected " + toRead);

                var messageHeader = Header.FromBytes(new ArraySegment<byte>(header));

                byte[] body = null;
                //read the body
                if (bodyLen != 0)
                {
                    body = new byte[bodyLen];

                    bytesRead = await ReadCountAsync(body, 0, bodyLen, _fileDescriptors);

                    if (bytesRead != bodyLen)
                        throw new ProtocolException("Message body length mismatch: " + bytesRead + " of expected " + bodyLen);
                }

                if (_fileDescriptors.Count < messageHeader.NumberOfFds)
                {
                    throw new ProtocolException("File descriptor length mismatch: " + _fileDescriptors.Count + " of expected " + messageHeader.NumberOfFds);
                }

                Message msg = new Message(
                    messageHeader,
                    body,
                    messageHeader.NumberOfFds == 0 ? null :
                        _fileDescriptors.Count == messageHeader.NumberOfFds ? _fileDescriptors.ToArray() :
                        _fileDescriptors.Take((int)messageHeader.NumberOfFds).ToArray()
                );

                _fileDescriptors.RemoveRange(0, (int)messageHeader.NumberOfFds);

                return msg;
            }
            catch
            {
                foreach(var fd in _fileDescriptors)
                {
                    CloseSafeHandle.close(fd.Handle);
                }
                throw;
            }
        }

        private async Task<int> ReadCountAsync(byte[] buffer, int offset, int count, List<UnixFd> fileDescriptors)
        {
            int read = 0;
            while (read < count)
            {
                int nread = await _socket.ReadAsync(buffer, offset + read, count - read, fileDescriptors);
                if (nread == 0)
                    break;
                read += nread;
            }
            return read;
        }

        private struct AuthenticationResult
        {
            public bool IsAuthenticated;
            public bool SupportsFdPassing;
            public Guid Guid;
        }

        class AuthCommand
        {
            public readonly string Value;
            private readonly List<string> _args = new List<string>();

            public AuthCommand(string value)
            {
                this.Value = value.Trim();
                _args.AddRange(Value.Split(' '));
            }

            public string this[int index]
            {
                get
                {
                    if (index >= _args.Count)
                        return String.Empty;
                    return _args[index];
                }
            }
        }

        private async Task DoSaslAuthenticationAsync(Guid guid)
        {
            var authenticationResult = await AuthenticateAsync();
            _socket.SupportsFdPassing = authenticationResult.SupportsFdPassing;
            if (guid != Guid.Empty)
            {
                if (guid != authenticationResult.Guid)
                {
                    throw new ConnectException("Authentication failure: Unexpected GUID");
                }
            }
        }

        private async Task<AuthenticationResult> AuthenticateAsync()
        {
            string initialData = null;
            if (_context.UserId != null)
            {
                byte[] bs = Encoding.ASCII.GetBytes(_context.UserId);
                initialData = ToHex(bs);
            }
            AuthenticationResult result;
            var commands = new[]
            {
                initialData != null ? "AUTH EXTERNAL " + initialData : null,
                "AUTH ANONYMOUS"
            };

            foreach (var command in commands)
            {
                if (command == null)
                {
                    continue;
                }
                result = await AuthenticateAsync(command);
                if (result.IsAuthenticated)
                {
                    return result;
                }
            }

            throw new ConnectException("Authentication failure");
        }

        private async Task<AuthenticationResult> AuthenticateAsync(string command)
        {
            AuthenticationResult result = default(AuthenticationResult);
            await WriteLineAsync(command);
            AuthCommand reply = await ReadReplyAsync();

            if (reply[0] == "OK")
            {
                result.IsAuthenticated = true;
                result.Guid = reply[1] != string.Empty ? Guid.ParseExact(reply[1], "N") : Guid.Empty;

                if (_socket.SupportsFdPassing)
                {
                    await WriteLineAsync("NEGOTIATE_UNIX_FD");
                    reply = await ReadReplyAsync();
                    result.SupportsFdPassing = reply[0] == "AGREE_UNIX_FD";
                }

                await WriteLineAsync("BEGIN");
                return result;
            }
            else if (reply[0] == "REJECTED")
            {
                return result;
            }
            else
            {
                await WriteLineAsync("ERROR");
                return result;
            }
        }

        private Task WriteLineAsync(string message)
        {
            message += "\r\n";
            var bytes = Encoding.ASCII.GetBytes(message);
            return _socket.SendAsync(bytes, 0, bytes.Length);
        }

        private async Task<AuthCommand> ReadReplyAsync()
        {
            byte[] buffer = new byte[1];
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int length = await ReadCountAsync(buffer, 0, buffer.Length, _fileDescriptors);
                byte b = buffer[0];
                if (length == 0)
                {
                    throw new IOException("Connection closed by peer");
                }
                else if (b == '\r')
                {
                    length = await ReadCountAsync(buffer, 0, buffer.Length, _fileDescriptors);
                    b = buffer[0];
                    if (b == '\n')
                    {
                        string ln = sb.ToString();
                        if (ln != string.Empty)
                        {
                            return new AuthCommand(ln);
                        }
                        else
                        {
                            throw new ProtocolException("Received empty authentication message from server");
                        }
                    }
                    throw new ProtocolException("Authentication messages from server must end with '\\r\\n'");
                }
                else
                {
                    sb.Append((char) b);
                }
            }
        }

        private static string ToHex(byte[] input)
        {
            StringBuilder result = new StringBuilder(input.Length * 2);
            string alfabeth = "0123456789abcdef";

            foreach (byte b in input)
            {
                result.Append(alfabeth[(int)(b >> 4)]);
                result.Append(alfabeth[(int)(b & 0xF)]);
            }

            return result.ToString();
        }

        public Task SendMessageAsync(Message message)
        {
            return _socket.SendAsync(message);
        }

        public void Dispose()
        {
            _socket?.Dispose();
        }
    }
}