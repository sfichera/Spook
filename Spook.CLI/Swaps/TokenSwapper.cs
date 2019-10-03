﻿using System.Collections.Generic;
using System.Linq;
using Phantasma.Numerics;
using Phantasma.Cryptography;
using Phantasma.Spook.Oracles;
using Phantasma.Core.Log;
using Phantasma.Core.Utils;
using Phantasma.Neo.Core;
using Phantasma.Blockchain;
using Phantasma.Domain;
using Phantasma.Pay.Chains;
using Phantasma.VM.Utils;
using Phantasma.Blockchain.Contracts;
using Phantasma.Contracts.Native;
using Phantasma.Core.Types;
using System;
using Phantasma.API;
using System.Threading;

namespace Phantasma.Spook.Swaps
{
    public struct PendingSwap
    {
        public readonly string platform;
        public readonly Hash hash;
        public readonly Address source;
        public readonly Address destination;

        public PendingSwap(string platform, Hash hash, Address source, Address destination)
        {
            this.platform = platform;
            this.hash = hash;
            this.source = source;
            this.destination = destination;
        }
    }

    public struct PendingSettle
    {
        public Hash sourceHash;
        public Hash destinationHash;
        public DateTime time;
    }

    public abstract class ChainWatcher
    {
        public readonly string PlatformName;
        public readonly TokenSwapper Swapper;
        public readonly string LocalAddress;

        protected ChainWatcher(TokenSwapper swapper, string platformName)
        {
            Swapper = swapper;
            this.PlatformName = platformName;
            this.LocalAddress = swapper.FindAddress(platformName);

            if (string.IsNullOrEmpty(LocalAddress))
            {
                throw new SwapException("Invalid address for neo swaps");
            }

            Swapper.logger.Message($"Listening for {platformName} swaps at address {LocalAddress}");
        }

        public abstract IEnumerable<PendingSwap> Update();
    }

    public class TokenSwapper : ITokenSwapper
    {
        public readonly NexusAPI NexusAPI;
        public Nexus Nexus => NexusAPI.Nexus;
        public readonly Logger logger;

        private readonly PhantasmaKeys SwapKeys;
        private readonly BigInteger MinimumFee;
        private readonly NeoAPI neoAPI;
        private readonly NeoScanAPI neoscanAPI;

        private readonly Dictionary<string, BigInteger> interopBlocks;
        private PlatformInfo[] platforms;

        private Dictionary<string, ChainWatcher> _finders = new Dictionary<string, ChainWatcher>();

        public TokenSwapper(PhantasmaKeys swapKey, NexusAPI nexusAPI, NeoScanAPI neoscanAPI, NeoAPI neoAPI, BigInteger minFee, Logger logger, Arguments arguments)
        {
            this.SwapKeys = swapKey;
            this.NexusAPI = nexusAPI;
            this.MinimumFee = minFee;

            this.neoAPI = neoAPI;
            this.neoscanAPI = neoscanAPI;
            this.logger = logger;

            this.interopBlocks = new Dictionary<string, BigInteger>();

            interopBlocks["phantasma"] = BigInteger.Parse(arguments.GetString("interop.phantasma.height", "0"));
            interopBlocks["neo"] = BigInteger.Parse(arguments.GetString("interop.neo.height", "4261049"));
            //interopBlocks["ethereum"] = BigInteger.Parse(arguments.GetString("interop.ethereum.height", "4261049"));
            

            /*
            foreach (var entry in interopBlocks)
            {
                BigInteger blockHeight = entry.Value;

                ChainInterop interop;

                switch (entry.Key)
                {
                    case "phantasma":
                        interop = new PhantasmaInterop(this, swapKey, blockHeight, nexusAPI);
                        break;

                    case "neo":
                        interop = new NeoInterop(this, swapKey, blockHeight, neoAPI, neoscanAPI);
                        break;

                    case "ethereum":
                        interop = new EthereumInterop(this, swapKey, blockHeight);
                        break;

                    default:
                        interop = null;
                        break;
                }

                if (interop != null)
                {
                    bool shouldAdd = true;

                    if (!(interop is PhantasmaInterop))
                    {
                        logger.Message($"{interop.Name}.Swap.Private: {interop.PrivateKey}");
                        logger.Message($"{interop.Name}.Swap.{interop.Name}: {interop.LocalAddress}");
                        logger.Message($"{interop.Name}.Swap.Phantasma: {interop.ExternalAddress}");

                        for (int i = 0; i < platforms.Length; i++)
                        {
                            var temp = platforms[i];
                            if (temp.platform == interop.Name)
                            {
                                if (temp.address != interop.LocalAddress)
                                {
                                    logger.Error($"{interop.Name} address mismatch, should be {temp.address}. Make sure you are using the proper swap seed.");
                                    shouldAdd = false;
                                }
                            }
                        }
                    }

                    if (shouldAdd)
                    {
                        AddInterop(interop);
                    }
                }
            }*/
        }

        internal IToken FindTokenByHash(string asset)
        {
            var hash = Hash.FromUnpaddedHex(asset);
            return Nexus.Tokens.Select(x => Nexus.GetTokenInfo(x)).Where(x => x.Hash == hash).FirstOrDefault();
        }

        internal string FindAddress(string platformName)
        {
            return platforms.Where(x => x.Name == platformName).Select(x => x.InteropAddresses[0].ExternalAddress).FirstOrDefault();
        }

        private Dictionary<Hash, PendingSwap> _pendingSwaps = new Dictionary<Hash, PendingSwap>();
        private Dictionary<Address, List<Hash>> _swapAddressMap = new Dictionary<Address, List<Hash>>();
        private Dictionary<Hash, Hash> _settlements = new Dictionary<Hash, Hash>();

        private List<PendingSettle> _pendingSettles = new List<PendingSettle>();

        private void MapSwap(Address address, Hash hash)
        {
            List<Hash> list;

            if (_swapAddressMap.ContainsKey(address))
            {
                list = _swapAddressMap[address];
            }
            else
            {
                list = new List<Hash>();
                _swapAddressMap[address] = list;
            }

            list.Add(hash);
        }

        public void Update()
        {
            if (this.platforms == null)
            {
                this.platforms = Nexus.Platforms.Select(x => Nexus.GetPlatformInfo(x)).ToArray();

                _finders["neo"] = new NeoInterop(this, interopBlocks["neo"], neoAPI, neoscanAPI, logger);
            }

            int i = 0;
            while (i < _pendingSettles.Count)
            {
                var settlement = _pendingSettles[i];
                var diff = DateTime.UtcNow - settlement.time;
                if (diff.TotalSeconds > 30)
                {
                    SettleTransaction(DomainSettings.PlatformName, DomainSettings.RootChainName, settlement.destinationHash);
                    _pendingSettles.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            foreach (var finder in _finders.Values)
            {
                var swaps = finder.Update();

                foreach (var swap in swaps)
                {
                    if (_pendingSwaps.ContainsKey(swap.hash))
                    {
                        continue;
                    }

                    logger.Message($"Detected {finder.PlatformName} swap: {swap.source} => {swap.destination}");
                    _pendingSwaps[swap.hash] = swap;
                    MapSwap(swap.source, swap.hash);
                    MapSwap(swap.destination, swap.hash);
                }
            }
        }

        public Hash SettleSwap(string sourcePlatform, string destPlatform, Hash sourceHash)
        {
            if (destPlatform == PhantasmaWallet.PhantasmaPlatform)
            {
                return SettleTransaction(sourcePlatform, sourcePlatform, sourceHash);
            }
            else 
            if (sourcePlatform != PhantasmaWallet.PhantasmaPlatform)
            {
                throw new SwapException("Invalid source platform");
            }

            var settleHash = GetSettleHash(sourcePlatform, sourceHash);
            if (settleHash != Hash.Null)
            {
                return settleHash;
            }

            switch (destPlatform)
            {
                case NeoWallet.NeoPlatform:
                    return SettleSwapToNeo(sourceHash);

                default:
                    return Hash.Null;
            }
        }

        public Hash GetSettleHash(string sourcePlatform, Hash sourceHash)
        {
            if (_settlements.ContainsKey(sourceHash))
            {
                return _settlements[sourceHash];
            }

            foreach (var settlement in _pendingSettles)
            {
                if (settlement.sourceHash == sourceHash)
                {
                    return settlement.destinationHash;
                }
            }

            var hash = (Hash)Nexus.RootChain.InvokeContract(Nexus.RootStorage, "interop", nameof(InteropContract.GetSettlement), sourcePlatform, sourceHash).ToObject();
            if (hash != Hash.Null && !_settlements.ContainsKey(sourceHash))
            {
                _settlements[sourceHash] = hash;
            }
            return hash;
        }

        private Hash SettleTransaction(string sourcePlatform, string chain, Hash txHash)
        {
            var script = new ScriptBuilder().
                AllowGas(SwapKeys.Address, Address.Null, MinimumFee, 9999).
                CallContract("interop", nameof(InteropContract.SettleTransaction), SwapKeys.Address, sourcePlatform, chain, txHash).
                SpendGas(SwapKeys.Address).
                EndScript();

            var tx = new Blockchain.Transaction(Nexus.Name, "main", script, Timestamp.Now + TimeSpan.FromMinutes(5));
            tx.Sign(SwapKeys);

            var bytes = tx.ToByteArray(true);

            var txData = Base16.Encode(bytes);
            var result = this.NexusAPI.SendRawTransaction(txData);
            if (result is SingleResult)
            {
                //var hash = (string)((SingleResult)result).value;
                return tx.Hash;
            }

            return Hash.Null;
        }

        public IEnumerable<ChainSwap> GetPendingSwaps(Address address)
        {
            if (_swapAddressMap.ContainsKey(address))
            {
                var swaps = _swapAddressMap[address].
                    Select(x => _pendingSwaps[x]).
                    Select(x => new ChainSwap(x.platform, x.platform, x.hash, DomainSettings.PlatformName, DomainSettings.RootChainName, Hash.Null));

                var dict = new Dictionary<Hash, ChainSwap>();
                foreach (var entry in swaps)
                {
                    dict[entry.sourceHash] = entry;
                }

                var keys = dict.Keys.ToArray();
                foreach (var hash in keys)
                {
                    var entry = dict[hash];
                    if (entry.destinationHash == Hash.Null)
                    {
                        var settleHash = GetSettleHash(entry.sourcePlatform, hash);
                        if (settleHash != Hash.Null)
                        {
                            entry.destinationHash = _settlements[entry.sourceHash];
                            dict[hash] = entry;
                        }
                    }
                }

                return dict.Values;
            }

            return Enumerable.Empty<ChainSwap>();
        }

        private Hash SettleSwapToNeo(Hash sourceHash)
        {
            return SettleSwapToExternal(NeoWallet.NeoPlatform, sourceHash, (destination, token, amount) =>
            {
                var total = UnitConversion.ToDecimal(amount, token.Decimals);

                var interopKeys = InteropUtils.GenerateInteropKeys(SwapKeys, Nexus.GenesisHash, NeoWallet.NeoPlatform);
                var neoKeys = Phantasma.Neo.Core.NeoKeys.FromWIF(interopKeys.ToWIF());

                var destAddress = NeoWallet.DecodeAddress(destination);

                Neo.Core.Transaction tx;
                if (token.Symbol == "NEO" || token.Symbol == "GAS")
                {
                    tx = neoAPI.SendAsset(neoKeys, destAddress, token.Symbol, total);
                }
                else
                {
                    var nep5 = neoAPI.GetToken(token.Symbol);
                    tx = nep5.Transfer(neoKeys, destAddress, total);
                }

                var txHash = Hash.Parse(tx.Hash.ToString());
                return txHash;
            });
        }

        private Hash SettleSwapToExternal(string destinationPlatform, Hash sourceHash, Func<Address, IToken, BigInteger, Hash> generator)
        {
            var oracleReader = Nexus.CreateOracleReader();
            var swap = oracleReader.ReadTransactionFromOracle(DomainSettings.PlatformName, DomainSettings.RootChainName, sourceHash);

            // TODO not support yet
            if (swap.Transfers.Length != 1)
            {
                logger.Warning($"Not implemented: Swap support for multiple transfers in a single transaction");
                return Hash.Null;
            }

            var transfer = swap.Transfers[0];

            var token = Nexus.GetTokenInfo(transfer.Symbol);

            var destHash = generator(transfer.destinationAddress, token, transfer.Value);
            if (destHash != Hash.Null)
            {
                _pendingSettles.Add(new PendingSettle() {sourceHash = sourceHash, destinationHash = destHash, time = DateTime.UtcNow });
            }

            return destHash;
        }
    }
}
