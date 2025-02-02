﻿using BeatTogether.LiteNetLib.Abstractions;
using BeatTogether.LiteNetLib.Headers;
using Krypton.Buffers;
using System;
using System.Net;
using System.Threading.Tasks;

namespace BeatTogether.LiteNetLib.Handlers
{
    public class MergedPacketHandler : BasePacketHandler<MergedHeader>
    {
        private readonly LiteNetServer _server;

        public MergedPacketHandler(
            LiteNetServer server)
        {
            _server = server;
        }

        public override Task Handle(EndPoint endPoint, MergedHeader packet, ref SpanBufferReader reader)
        {
            while (reader.RemainingSize > 0)
            {
                ReadOnlySpan<byte> newPacket;
                try
                {
                    ushort size = reader.ReadUInt16();
                    newPacket = reader.ReadBytes(size);
                }
                catch(EndOfBufferException) { return Task.CompletedTask; }
                _server.HandlePacket(endPoint, newPacket);
            }
            return Task.CompletedTask;
        }
    }
}
