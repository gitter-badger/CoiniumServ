﻿#region License
// 
//     CoiniumServ - Crypto Currency Mining Pool Server Software
//     Copyright (C) 2013 - 2014, CoiniumServ Project - http://www.coinium.org
//     http://www.coiniumserv.com - https://github.com/CoiniumServ/CoiniumServ
// 
//     This software is dual-licensed: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// 
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//    
//     For the terms of this license, see licenses/gpl_v3.txt.
// 
//     Alternatively, you can license this software under a commercial
//     license or white-label it as set out in licenses/commercial.txt.
// 
#endregion

using CoiniumServ.Algorithms;
using CoiniumServ.Coin.Helpers;
using CoiniumServ.Daemon;
using CoiniumServ.Daemon.Errors;
using CoiniumServ.Daemon.Exceptions;
using CoiniumServ.Utils.Helpers;
using Serilog;

namespace CoiniumServ.Pools
{
    public class NetworkInfo:INetworkInfo
    {
        public double Difficulty { get; private set; }
        public int Round { get; private set; }
        public ulong Hashrate { get; private set; }
        public string CoinVersion { get; private set; }
        public int ProtocolVersion { get; private set; }
        public int WalletVersion { get; private set; }
        public bool Testnet { get; private set; }
        public long Connections { get; private set; }
        public string Errors { get; private set; }
        public bool Healthy { get; private set; }
        public string ServiceResponse { get; private set; } // todo implement this too for /pool/COIN/network

        private readonly IDaemonClient _daemonClient;

        private readonly IHashAlgorithm _hashAlgorithm;

        private readonly IPoolConfig _poolConfig;

        private readonly ILogger _logger;

        // todo: add %51 hash power detection support.

        public NetworkInfo(IDaemonClient daemonClient, IHashAlgorithm hashAlgorithm, IPoolConfig poolConfig)
        {
            _daemonClient = daemonClient;
            _hashAlgorithm = hashAlgorithm;
            _poolConfig = poolConfig;
            _logger = Log.ForContext<NetworkInfo>().ForContext("Component", poolConfig.Coin.Name);

            DetectProofOfStakeCoin(); // detect if we are running on a proof-of-stake coin.
            DetectSubmitBlockSupport(); // detect if the coin daemon supports submitblock call.
            Recache(); // recache the data initially.
            PrintNetworkInfo(); // print the collected network info.
        }

        public void Recache()
        {
            try // read getinfo() based data.
            {
                var info = _daemonClient.GetInfo();

                // read data.
                CoinVersion = info.Version;
                ProtocolVersion = info.ProtocolVersion;
                WalletVersion = info.WalletVersion;
                Testnet = info.Testnet;
                Connections = info.Connections;
                Errors = info.Errors;

                // check if our network connection is healthy.
                Healthy = Connections >= 0 && string.IsNullOrEmpty(Errors);
            }
            catch (RpcException e)
            {
                _logger.Error("Can not read getinfo(): {0:l}", e.Message);
                Healthy = false; // set healthy status to false as we couldn't get a reply.
            }

            try // read mininginfo() based data.
            {
                var miningInfo = _daemonClient.GetMiningInfo();

                // read data.
                Hashrate = miningInfo.NetworkHashPerSec;
                Difficulty = miningInfo.Difficulty;
                Round = miningInfo.Blocks + 1;
            }
            catch (RpcException e)
            {
                _logger.Error("Can not read mininginfo() - the coin may not support the request: {0:l}", e.Message);
                Hashrate = 0;
                Difficulty = 0;
                Round = -1;
                Healthy = false; // set healthy status to false as we couldn't get a reply.
            }
        }

        private void PrintNetworkInfo()
        {
            _logger.Information("symbol: {0:l} algorithm: {1:l} " +
                                "version: {2:l} protocol: {3} wallet: {4} " +
                                "network difficulty: {5:0.00000000} block difficulty: {6:0.00} network hashrate: {7:l} " +
                                "network: {8:l} peers: {9} blocks: {10} errors: {11:l} ",
                _poolConfig.Coin.Symbol,
                _poolConfig.Coin.Algorithm,
                CoinVersion,
                ProtocolVersion,
                WalletVersion,
                Difficulty,
                Difficulty*_hashAlgorithm.Multiplier,
                Hashrate.GetReadableHashrate(),
                Testnet ? "testnet" : "mainnet",
                Connections,
                Round - 1,
                string.IsNullOrEmpty(Errors) ? "none" : Errors);
        }


        private void DetectSubmitBlockSupport()
        {
            // issue a submitblock() call too see if it's supported.
            // If the coin supports the submitblock() call it's should return a RPC_DESERIALIZATION_ERROR (-22) - 'Block decode failed' as we just supplied an empty string as block hash.
            // otherwise if it doesn't support the call, it should return a RPC_METHOD_NOT_FOUND (-32601) - 'Method not found' error.

            try
            {
                var response = _daemonClient.SubmitBlock(string.Empty);
            }
            catch (RpcErrorException e)
            {
                if (e.Code == (int)RpcErrorCode.RPC_METHOD_NOT_FOUND) // 'Method not found' error.
                    _poolConfig.Coin.Options.SubmitBlockSupported = false; // the coin doesn't support submitblock().
                else if (e.Code == (int)RpcErrorCode.RPC_DESERIALIZATION_ERROR) // 'Block decode failed' error.
                    _poolConfig.Coin.Options.SubmitBlockSupported = true; // the coin supports submitblock().
                else // we shouldn't be really recieving any other errors.
                    _logger.Error("Recieved an unexpected response for DetectSubmitBlockSupport() - {0}, {1:l}", e.Code, e.Message);
            }
        }

        private void DetectProofOfStakeCoin()
        {
            // use getdifficulty() to determine if it's POS coin.

            try
            {
                /*  By default proof-of-work coins return a floating point as difficulty (https://en.bitcoin.it/wiki/Original_Bitcoin_client/API_calls_lis).
                 *  Though proof-of-stake coins returns a json-object;
                 *  { "proof-of-work" : 41867.16992903, "proof-of-stake" : 0.00390625, "search-interval" : 0 }
                 *  So basically we can use this info to determine if assigned coin is a proof-of-stake one.
                 */

                var response = _daemonClient.MakeRawRequest("getdifficulty");
                if (response.Contains("proof-of-stake")) // if response contains proof-of-stake field
                    _poolConfig.Coin.Options.IsProofOfStakeHybrid = true; // then automatically set coin-config.IsPOS to true.
            }
            catch (RpcException e)
            {
                _logger.Error("Can not read getdifficulty() - the coin may not support the request: {0:l}", e.Message);
            }
        }
    }
}
