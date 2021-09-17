﻿using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using PacketDotNet;
using System;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using WinDivertSharp;

namespace FastGithub.Dns
{
    /// <summary>
    /// dns拦截器
    /// </summary>   
    [SupportedOSPlatform("windows")]
    sealed class DnsInterceptor
    {
        private const string DNS_FILTER = "udp.DstPort == 53";
        private readonly FastGithubConfig fastGithubConfig;
        private readonly ILogger<DnsInterceptor> logger;
        private readonly TimeSpan ttl = TimeSpan.FromMinutes(1d);

        /// <summary>
        /// 刷新DNS缓存
        /// </summary>    
        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
        private static extern void DnsFlushResolverCache();

        /// <summary>
        /// dns投毒后台服务
        /// </summary>
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public DnsInterceptor(
            FastGithubConfig fastGithubConfig,
            ILogger<DnsInterceptor> logger)
        {
            this.fastGithubConfig = fastGithubConfig;
            this.logger = logger;
        }

        /// <summary>
        /// DNS拦截
        /// </summary>
        /// <param name="cancellationToken"></param>
        public void Intercept(CancellationToken cancellationToken)
        {
            var handle = WinDivert.WinDivertOpen(DNS_FILTER, WinDivertLayer.Network, 0, WinDivertOpenFlags.None);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            cancellationToken.Register(hwnd =>
            {
                WinDivert.WinDivertClose((IntPtr)hwnd!);
                DnsFlushResolverCache();
            }, handle);

            var packetLength = 0U;
            var packetBuffer = new byte[ushort.MaxValue];
            using var winDivertBuffer = new WinDivertBuffer(packetBuffer);
            var winDivertAddress = new WinDivertAddress();

            DnsFlushResolverCache();
            while (cancellationToken.IsCancellationRequested == false)
            {
                if (WinDivert.WinDivertRecv(handle, winDivertBuffer, ref winDivertAddress, ref packetLength))
                {
                    try
                    {
                        this.ProcessDnsPacket(packetBuffer, ref winDivertAddress, ref packetLength);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning(ex.Message);
                    }

                    WinDivert.WinDivertHelperCalcChecksums(winDivertBuffer, packetLength, ref winDivertAddress, WinDivertChecksumHelperParam.All);
                    WinDivert.WinDivertSend(handle, winDivertBuffer, packetLength, ref winDivertAddress);
                }
            }
        }

        /// <summary>
        /// 处理DNS数据包
        /// </summary>
        /// <param name="packetBuffer"></param>
        /// <param name="winDivertAddress"></param>
        /// <param name="packetLength"></param>
        private void ProcessDnsPacket(byte[] packetBuffer, ref WinDivertAddress winDivertAddress, ref uint packetLength)
        {
            var packetData = packetBuffer.AsSpan(0, (int)packetLength).ToArray();
            var packet = Packet.ParsePacket(LinkLayers.Raw, packetData);
            var ipPacket = (IPPacket)packet.PayloadPacket;
            var udpPacket = (UdpPacket)ipPacket.PayloadPacket;

            var request = Request.FromArray(udpPacket.PayloadData);
            if (request.OperationCode != OperationCode.Query)
            {
                return;
            }

            var question = request.Questions.FirstOrDefault();
            if (question == null || question.Type != RecordType.A)
            {
                return;
            }

            var domain = question.Name;
            if (this.fastGithubConfig.IsMatch(domain.ToString()) == false)
            {
                return;
            }

            // 反转ip
            var sourceAddress = ipPacket.SourceAddress;
            ipPacket.SourceAddress = ipPacket.DestinationAddress;
            ipPacket.DestinationAddress = sourceAddress;

            // 反转端口
            var sourcePort = udpPacket.SourcePort;
            udpPacket.SourcePort = udpPacket.DestinationPort;
            udpPacket.DestinationPort = sourcePort;

            // 设置dns响应
            var response = Response.FromRequest(request);
            var record = new IPAddressResourceRecord(domain, IPAddress.Loopback, this.ttl);
            response.AnswerRecords.Add(record);
            udpPacket.PayloadData = response.ToArray();

            // 修改数据内容和数据长度
            packet.Bytes.CopyTo(packetBuffer, 0);
            packetLength = (uint)packet.Bytes.Length;

            // 反转方向
            if (winDivertAddress.Direction == WinDivertDirection.Inbound)
            {
                winDivertAddress.Direction = WinDivertDirection.Outbound;
            }
            else
            {
                winDivertAddress.Direction = WinDivertDirection.Inbound;
            }
        }
    }
}
