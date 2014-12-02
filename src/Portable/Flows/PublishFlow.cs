﻿using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hermes.Packets;
using Hermes.Storage;

namespace Hermes.Flows
{
	public abstract class PublishFlow : IPublishFlow
	{
		protected readonly IRepository<ClientSession> sessionRepository;
		protected readonly ProtocolConfiguration configuration;

		protected PublishFlow (IRepository<ClientSession> sessionRepository, 
			ProtocolConfiguration configuration)
		{
			this.sessionRepository = sessionRepository;
			this.configuration = configuration;
		}

		public abstract Task ExecuteAsync (string clientId, IPacket input, IChannel<IPacket> channel);

		public async Task SendAckAsync (string clientId, IFlowPacket ack, IChannel<IPacket> channel, PendingMessageStatus status = PendingMessageStatus.PendingToSend)
		{
			if((ack.Type == PacketType.PublishReceived || ack.Type == PacketType.PublishRelease) && 
				status == PendingMessageStatus.PendingToSend)
				this.SavePendingAcknowledgement (ack, clientId);

			if (!channel.IsConnected)
				return;

			if(ack.Type == PacketType.PublishReceived)
				this.MonitorAck<PublishRelease> (ack, channel);
			else if (ack.Type == PacketType.PublishRelease)
				this.MonitorAck<PublishComplete> (ack, channel);

			await channel.SendAsync (ack);
		}

		protected void MonitorAck<T>(IFlowPacket sentPacket, IChannel<IPacket> channel)
			where T : IFlowPacket
		{
			channel.Receiver
				.OfType<T> ()
				.FirstAsync (ack => ack.PacketId == sentPacket.PacketId)
				.Timeout (new TimeSpan (0, 0, this.configuration.WaitingTimeoutSecs))
				.Subscribe (_ => { }, async ex => {
					this.MonitorAck<T> (sentPacket, channel);

					await channel.SendAsync (sentPacket);
				});
		}

		private void SavePendingAcknowledgement(IFlowPacket ack, string clientId)
		{
			if (ack.Type != PacketType.PublishReceived && ack.Type != PacketType.PublishRelease)
				return;

			var unacknowledgeMessage = new PendingAcknowledgement {
				PacketId = ack.PacketId,
				Type = ack.Type
			};

			var session = this.sessionRepository.Get (s => s.ClientId == clientId);

			session.PendingAcknowledgements.Add (unacknowledgeMessage);

			this.sessionRepository.Update (session);
		}

		protected void RemovePendingAcknowledgement(string clientId, ushort packetId, PacketType type)
		{
			var session = this.sessionRepository.Get (s => s.ClientId == clientId);
			var pendingAcknowledgement = session.PendingAcknowledgements
				.FirstOrDefault(u => u.Type == type && u.PacketId == packetId);

			session.PendingAcknowledgements.Remove (pendingAcknowledgement);

			this.sessionRepository.Update (session);
		}
	}
}
