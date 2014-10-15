﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using NBitcoin.DataEncoders;
using NBitcoin.OpenAsset;
using NBitcoin.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NBitcoin.Indexer.Tests
{
    public class TestClass
    {
        [Fact]
        public void CanSpreadBytes()
        {
            var bytes =
                Helper.SerializeList(Enumerable.Range(0, 300000).Select(e => new AddressEntry.Entity.IntCompactVarInt((uint)e)).ToArray());

            DynamicTableEntity entity = new DynamicTableEntity();
            Helper.SetEntityProperty(entity, "a", bytes);
            var actualBytes = Helper.GetEntityProperty(entity, "a");
            Assert.True(actualBytes.SequenceEqual(bytes));
        }
        [Fact]
        public void DoesNotCrashExtractingAddressFromBigTransaction()
        {
            var tx = new Transaction(Encoders.Hex.DecodeData(File.ReadAllText("Data/BigTransaction.txt")));
            var txId = tx.GetHash();
            var result = AddressEntry.Entity.ExtractFromTransaction(tx, txId);
            foreach (var e in result)
            {
                var entity = e.Value.CreateTableEntity();
            }
        }
        [Fact]
        public void CanUploadBlobDirectoryToAzure()
        {
            using (var tester = CreateTester())
            {
                tester.Indexer.TaskCount = 15;
                tester.Indexer.BlkCount = 1;
                tester.Indexer.FromBlk = 0;
                Assert.Equal(138, tester.Indexer.IndexBlocks());
                Assert.Equal(0, tester.Indexer.IndexBlocks());
            }
        }
        [Fact]
        public void CanUploadTransactionsToAzure()
        {
            using (var tester = CreateTester())
            {

                tester.Indexer.TaskCount = 15;
                tester.Indexer.BlkCount = 1;
                tester.Indexer.FromBlk = 0;
                Assert.Equal(138, tester.Indexer.IndexTransactions());
                Assert.Equal(0, tester.Indexer.IndexTransactions());
            }
        }


        [Fact]
        public void CanIndexMeempool()
        {
            using (var tester = CreateTester())
            {
                var node = tester.CreateLocalNode();
                var sender = new Key().PubKey;
                var receiver = new Key().PubKey;

                var t1 = new Transaction()
                        {
                            Outputs = 
							{
								new TxOut("10.0",sender.GetAddress(Network.Main))
							}
                        };
                var t2 = new Transaction()
                        {
                            Inputs = 
							{
								new TxIn(new OutPoint(t1.GetHash(),0))
								{
									ScriptSig = new PayToPubkeyHashTemplate()
												.GenerateScriptSig(sig,sender)
								}
							},
                            Outputs = 
							{
								new TxOut("2.0",receiver.GetAddress(Network.Main)),
								new TxOut("8.0",sender.GetAddress(Network.Main))
							}
                        };

                node.AddToMempool(t1, t2);

                Assert.Equal(2, tester.Indexer.IndexMempool());

                var tx = tester.Client.GetTransaction(t1.GetHash());
                Assert.NotNull(tx);
                Assert.True(tx.MempoolDate != null);
                Assert.True(tx.BlockIds.Length == 0);

                Assert.Equal(0, tester.Indexer.IndexMempool());

                var t3 = new Transaction()
                        {
                            Inputs = 
							{
								new TxIn(new OutPoint(t2.GetHash(),1))
								{
									ScriptSig = new PayToPubkeyHashTemplate()
												.GenerateScriptSig(sig,sender)
								}
							},
                            Outputs = 
							{
								new TxOut("2.1",receiver.GetAddress(Network.Main)),
								new TxOut("5.9",sender.GetAddress(Network.Main))
							}
                        };
                node.AddToMempool(t3);
                Assert.Equal(1, tester.Indexer.IndexMempool());

                var entries = tester.Client.GetEntries(sender);
                AssertContainsMoney("10.0", entries);
                AssertContainsMoney("-2.0", entries);
                AssertContainsMoney("-2.1", entries);

                Assert.True(entries.All(e => e.BlockIds.Length == 0));

                var store = tester.CreateLocalBlockStore();
                store.Append(new Block()
                {
                    Transactions = new List<Transaction>()
					{
						t1,
						t2,
						t3
					}
                });
                tester.Indexer.IndexBalances();
                entries = tester.Client.GetEntries(sender);
                Assert.True(entries.All(e => e.BlockIds.Length == 1));
            }
        }

        [Fact]
        public void CanImportMainChain()
        {
            using (var tester = CreateTester())
            {
                var node = tester.CreateLocalNode();
                var chain = new Chain(Network.Main);

                node.Generator.Generate();
                var fork = node.Generator.Generate();
                var firstTip = node.Generator.Generate();
                tester.Indexer.IndexMainChain();

                var result = tester.Client.GetChainChangesUntilFork(chain.Tip, true).ToList();
                Assert.Equal(result[0].BlockId, firstTip.GetHash());
                Assert.Equal(result.Last().BlockId, chain.Tip.HashBlock);
                Assert.Equal(result.Last().Height, chain.Tip.Height);
                Assert.Equal(result.Count, 4);

                result = tester.Client.GetChainChangesUntilFork(chain.Tip, false).ToList();
                Assert.Equal(result[0].BlockId, firstTip.GetHash());
                Assert.NotEqual(result.Last().BlockId, chain.Tip.HashBlock);
                Assert.Equal(result.Count, 3);

                Assert.Equal(firstTip.GetHash(), tester.Client.GetBestBlock().BlockId);

                result.UpdateChain(chain);

                Assert.Equal(firstTip.GetHash(), chain.Tip.HashBlock);

                node.Generator.Chain.SetTip(fork.Header);
                node.Generator.Generate();
                node.Generator.Generate();
                var secondTip = node.Generator.Generate();

                tester.Indexer.IndexMainChain();
                Assert.Equal(secondTip.GetHash(), tester.Client.GetBestBlock().BlockId);

                result = tester.Client.GetChainChangesUntilFork(chain.Tip, false).ToList();
                result.UpdateChain(chain);
                Assert.Equal(secondTip.GetHash(), chain.Tip.HashBlock);

                var ultimateTip = node.Generator.Generate(100);
                tester.Indexer.IndexMainChain();
                result = tester.Client.GetChainChangesUntilFork(chain.Tip, false).ToList();

                Assert.Equal(ultimateTip.Header.GetHash(), result[0].BlockId);
                Assert.Equal(tester.Client.GetBestBlock().BlockId, result[0].BlockId);
                result.UpdateChain(chain);
                Assert.Equal(ultimateTip.Header.GetHash(), chain.Tip.HashBlock);
            }
        }

        //[Fact]
        //public void CanGetMultipleEntries()
        //{
        //	var client = new IndexerClient(new IndexerConfiguration()
        //	{
        //		Network = Network.Main,

        //	});

        //	Stopwatch watch = new Stopwatch();
        //	watch.Start();
        //	for(int i = 0 ; i < 10 ; i++)
        //	{
        //		var r = client.GetAllEntries(JsonConvert.DeserializeObject<string[]>(File.ReadAllText("C:/Addresses.txt")).Select(n => new BitcoinScriptAddress(n, Network.Main)).ToArray());
        //	}
        //	watch.Stop();
        //}

        public List<ChainChange> SeeChainChanges(Chain chain)
        {
            chain.Changes.Rewind();
            return chain.Changes.Enumerate().ToList();
        }

        [Fact]
        public void CanGeneratePartitionKey()
        {
            HashSet<string> results = new HashSet<string>();
            while (results.Count != 4096)
            {
                results.Add(Helper.GetPartitionKey(12, RandomUtils.GetBytes(3), 0, 3));
            }
        }

        TransactionSignature sig = new TransactionSignature(Encoders.Hex.DecodeData("304602210095050cbad0bc3bad2436a651810e83f21afb1cdf75d74a13049114958942067d02210099b591d52665597fd88c4a205fe3ef82715e5a125e0f2ae736bf64dc634fba9f01"));
        [Fact]
        public void CanUploadBalancesToAzure()
        {
            using (var tester = CreateTester())
            {
                var node = tester.CreateLocalNode();
                var store = tester.CreateLocalBlockStore();
                var sender = new Key().PubKey;
                var receiver = new Key().PubKey;
                var b1 = new Block()
                {
                    Header =
                    {
                        Nonce = RandomUtils.GetUInt32(),
                        HashPrevBlock = Network.Main.GetGenesis().GetHash()
                    },
                    Transactions =
					{
						new Transaction()
						{
							Outputs = 
							{
								new TxOut("10.0",sender.GetAddress(Network.Main))
							}
						}
					}
                };
                store.Append(b1);
                var b2 = new Block()
                {
                    Header =
                    {
                        Nonce = RandomUtils.GetUInt32(),
                        HashPrevBlock = b1.GetHash()
                    },
                    Transactions =
					{
						new Transaction()
						{
							Inputs = 
							{
								new TxIn(new OutPoint(b1.Transactions[0].GetHash(),0))
								{
									ScriptSig = new PayToPubkeyHashTemplate()
												.GenerateScriptSig(sig,sender)
								}
							},
							Outputs = 
							{
								new TxOut("2.0",receiver.GetAddress(Network.Main)),
								new TxOut("8.0",sender.GetAddress(Network.Main))
							}
						}
					}
                };
                store.Append(b2);

                tester.Indexer.Configuration.BlockDirectory = store.Folder.FullName;
                tester.Indexer.TaskCount = 15;

                tester.Indexer.IndexBlocks();
                tester.Indexer.IndexTransactions();
                tester.Indexer.IndexBalances();

                var entries = tester.Client.GetEntries(sender);
                Assert.Equal(2, entries.Length);
                var entry = AssertContainsMoney("10.0", entries);
                Assert.True(new[] { new OutPoint(b1.Transactions[0].GetHash(), 0) }.SequenceEqual(entry.ReceivedCoins.Select(c => c.OutPoint)));
                Assert.Equal(entry.BlockIds[0], b1.GetHash());

                entry = AssertContainsMoney("-2.0", entries);
                Assert.NotNull(entry.SpentCoins);
                Assert.Equal(1, entry.SpentCoins.Count);
                Assert.Equal(b1.Transactions[0].GetHash(), entry.SpentCoins[0].OutPoint.Hash);
                Assert.Equal(0, (int)entry.SpentCoins[0].OutPoint.N);

                entries = tester.Client.GetEntries(receiver);
                Assert.Equal(1, entries.Length);
                AssertContainsMoney("2.0", entries);
                entries = tester.Client.GetEntries(receiver);

                var b3 = new Block()
                {
                    Header =
                    {
                        Nonce = RandomUtils.GetUInt32(),
                        HashPrevBlock = b2.GetHash()
                    },
                    Transactions =
					{
						new Transaction()
						{
							Inputs = 
							{
								new TxIn(new OutPoint(new uint256("bf6b530a4fd7fb107f52a8c433bc10e9388d129a6bb26567685e8b0674a76a2a"),0))
								{
									ScriptSig = new PayToPubkeyHashTemplate()
												.GenerateScriptSig(sig,sender)
								}
							},
							Outputs = 
							{
								new TxOut("2.1",receiver.GetAddress(Network.Main)),
								new TxOut("8.0",sender.GetAddress(Network.Main))
							}
						}
					}
                };
                store.Append(b3);

                tester.Indexer.IndexBlocks();

                foreach (var block in store.Enumerate(true, 0))
                {
                    node.Generator.Chain.SetTip(block.Item.Header);
                }
                tester.Indexer.IndexMainChain();
                tester.Indexer.IndexTransactions();
                tester.Indexer.IndexBalances();

                entries = tester.Client.GetEntries(receiver);
                AssertContainsMoney("2.1", entries);
                Chain chain = new Chain(Network.Main);
                tester.Client
                    .GetChainChangesUntilFork(chain.Tip, false)
                    .UpdateChain(chain);
                entries.Select(e => e.FetchConfirmedBlock(chain)).ToArray();

                entries = tester.Client.GetEntries(sender);
                AssertContainsMoney(null, entries);

                var b4 = new Block()
                {
                    Header =
                    {
                        Nonce = RandomUtils.GetUInt32(),
                        HashPrevBlock = b3.GetHash(),
                    },
                    Transactions =
					{
						new Transaction()
						{
							Inputs = 
							{
								new TxIn(new OutPoint(b3.Transactions[0].GetHash(),0))
								{
									ScriptSig = new PayToPubkeyHashTemplate()
												.GenerateScriptSig(sig,receiver)
								}
							},
							Outputs = 
							{
								new TxOut("1.5",sender.GetAddress(Network.Main)),
							}
						}
					}
                };
                store.Append(b4);

                tester.Indexer.IndexBlocks();
                tester.Indexer.IndexTransactions();
                tester.Indexer.IndexBalances();

                var tx = tester.Client.GetTransaction(false, b4.Transactions[0].GetHash());
                Assert.Null(tx.SpentCoins);

                tx = tester.Client.GetTransaction(true, b4.Transactions[0].GetHash());
                Assert.NotNull(tx.SpentCoins);
                Assert.Equal(Money.Parse("0.60"), tx.Fees);

                Assert.True(tx.SpentCoins[0].TxOut.ToBytes().SequenceEqual(b3.Transactions[0].Outputs[0].ToBytes()));
                tx = tester.Client.GetTransaction(false, b4.Transactions[0].GetHash());
                Assert.NotNull(tx);
            }
        }

        [DebuggerHidden]
        private AddressEntry AssertContainsMoney(Money expected, AddressEntry[] entries)
        {
            var entry = entries.FirstOrDefault(e => e.BalanceChange == expected);
            Assert.True(entry != null);
            return entry;
        }

        [Fact]
        public void CanGetBlock()
        {
            using (var tester = CreateTester("cached"))
            {
                tester.Cached = true;
                tester.ImportCachedBlocks();

                var block = tester.Client.GetBlock(tester.KnownBlockId);
                Assert.True(block.CheckMerkleRoot());
                block = tester.Client.GetBlock(tester.UnknownBlockId);
                Assert.Null(block);
            }
        }
        [Fact]
        public void CanGetTransaction()
        {
            using (var tester = CreateTester("cached"))
            {
                tester.Cached = true;
                tester.ImportCachedBlocks();
                tester.ImportCachedTransactions();

                var tx = tester.Client.GetTransaction(tester.KnownTransactionId);
                Assert.True(tx.Transaction.GetHash() == tester.KnownTransactionId);
                Assert.True(tx.TransactionId == tester.KnownTransactionId);
                Assert.True(tx.BlockIds[0] == tester.KnownBlockId);

                tx = tester.Client.GetTransaction(tester.UnknownTransactionId);
                Assert.Null(tx);
            }
        }

        [Fact]
        public void CanGetColoredTransaction()
        {
            using (var tester = CreateTester())
            {
                var blockStore = tester.CreateLocalBlockStore();
                tester.Indexer.Configuration.BlockDirectory = blockStore.Folder.FullName;
                var ccTester = new ColoredCoinTester("CanColorizeTransferTransaction");
                blockStore.Append(CreateBlock(ccTester));
                tester.Indexer.IndexBlocks();
                tester.Indexer.IndexTransactions();
                var txRepo = new IndexerTransactionRepository(tester.Indexer.Configuration);
                var indexedTx = txRepo.Get(ccTester.TestedTxId);
                Assert.NotNull(indexedTx);
                Assert.Null(txRepo.Get(tester.UnknownTransactionId));

                var ccTxRepo = new IndexerColoredTransactionRepository(tester.Indexer.Configuration);
                var colored = ccTxRepo.Get(ccTester.TestedTxId);
                Assert.Null(colored);

                colored = ColoredTransaction.FetchColors(ccTester.TestedTxId, ccTxRepo);
                Assert.NotNull(colored);

                colored = ccTxRepo.Get(ccTester.TestedTxId);
                Assert.NotNull(colored);
            }
        }

        private Block CreateBlock(ColoredCoinTester ccTester)
        {
            var block = new Block();
            block.Transactions.AddRange(ccTester.Transactions);
            return block;
        }



        private IndexerTester CreateTester([CallerMemberName]string folder = null)
        {
            return new IndexerTester(folder);
        }
    }

    class ColoredCoinTester
    {
        public ColoredCoinTester([CallerMemberName]string test = null)
        {
            var testcase = JsonConvert.DeserializeObject<TestCase[]>(File.ReadAllText("Data/openasset-known-tx.json"))
                .First(t => t.test == test);
            NoSqlTransactionRepository repository = new NoSqlTransactionRepository();
            foreach (var tx in testcase.txs)
            {
                var txObj = new Transaction(tx);
                Transactions.Add(txObj);
                repository.Put(txObj.GetHash(), txObj);
            }
            TestedTxId = new uint256(testcase.testedtx);
            Repository = new NoSqlColoredTransactionRepository(repository, new InMemoryNoSqlRepository());
        }


        public IColoredTransactionRepository Repository
        {
            get;
            set;
        }

        public uint256 TestedTxId
        {
            get;
            set;
        }

        public string AutoDownloadMissingTransaction(Action act)
        {
            StringBuilder builder = new StringBuilder();
            while (true)
            {
                try
                {
                    act();
                    break;
                }
                catch (TransactionNotFoundException ex)
                {
                    WebClient client = new WebClient();
                    var result = client.DownloadString("http://btc.blockr.io/api/v1/tx/raw/" + ex.TxId);
                    var json = JObject.Parse(result);
                    var tx = new Transaction(json["data"]["tx"]["hex"].ToString());

                    builder.AppendLine("\"" + json["data"]["tx"]["hex"].ToString() + "\",\r\n");
                    Repository.Transactions.Put(tx.GetHash(), tx);
                }
            }
            return builder.ToString();
        }

        public List<Transaction> Transactions = new List<Transaction>();
    }

    class TestCase
    {
        public string test
        {
            get;
            set;
        }
        public string testedtx
        {
            get;
            set;
        }
        public string[] txs
        {
            get;
            set;
        }
    }
}
