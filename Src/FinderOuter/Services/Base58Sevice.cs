﻿// The FinderOuter
// Copyright (c) 2020 Coding Enthusiast
// Distributed under the MIT software license, see the accompanying
// file LICENCE or http://www.opensource.org/licenses/mit-license.php.

using Autarkysoft.Bitcoin;
using Autarkysoft.Bitcoin.Cryptography.Asymmetric.EllipticCurve;
using Autarkysoft.Bitcoin.Cryptography.Asymmetric.KeyPairs;
using Autarkysoft.Bitcoin.Encoders;
using FinderOuter.Backend;
using FinderOuter.Backend.Cryptography.Asymmetric.EllipticCurve;
using FinderOuter.Backend.Cryptography.Hashing;
using FinderOuter.Backend.ECC;
using FinderOuter.Models;
using FinderOuter.Services.Comparers;
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FinderOuter.Services
{
    public class Base58Sevice
    {
        public Base58Sevice(IReport rep)
        {
            inputService = new InputService();
            report = rep;
        }


        private readonly IReport report;
        private readonly InputService inputService;
        private ICompareService comparer;
        private uint[] powers58, precomputed;
        private ulong[] multPow58, preC;
        private int[] missingIndexes;
        private int missCount;
        private string keyToCheck;


        public enum InputType
        {
            PrivateKey,
            Address,
            Bip38
        }

        private void Initialize(ReadOnlySpan<char> key, char missingChar, InputType keyType)
        {
            // Compute 58^n for n from 0 to inputLength as uint[]

            byte[] padded;
            int uLen = keyType switch
            {
                InputType.PrivateKey => 10, // Maximum result (58^52) is 39 bytes = 39/4 = 10 uint
                InputType.Address => 7, // Maximum result (58^35) is 26 bytes = 26/4 = 7 uint
                InputType.Bip38 => 11, // Maximum result (58^58) is 43 bytes = 43/4 = 11 uint
                _ => throw new ArgumentException("Input type is not defined yet."),
            };
            powers58 = new uint[key.Length * uLen];
            padded = new byte[4 * uLen];
            precomputed = new uint[uLen];

            for (int i = 0, j = 0; i < key.Length; i++)
            {
                BigInteger val = BigInteger.Pow(58, i);
                byte[] temp = val.ToByteArrayExt(false, true);

                Array.Clear(padded, 0, padded.Length);
                Buffer.BlockCopy(temp, 0, padded, 0, temp.Length);

                for (int k = 0; k < padded.Length; j++, k += 4)
                {
                    powers58[j] = (uint)(padded[k] << 0 | padded[k + 1] << 8 | padded[k + 2] << 16 | padded[k + 3] << 24);
                }
            }

            // calculate what we already have and store missing indexes
            int mis = 0;
            for (int i = key.Length - 1, j = 0; i >= 0; i--)
            {
                if (key[i] != missingChar)
                {
                    ulong carry = 0;
                    ulong val = (ulong)ConstantsFO.Base58Chars.IndexOf(key[i]);
                    for (int k = uLen - 1; k >= 0; k--, j++)
                    {
                        ulong result = checked((powers58[j] * val) + precomputed[k] + carry);
                        precomputed[k] = (uint)result;
                        carry = (uint)(result >> 32);
                    }
                }
                else
                {
                    missingIndexes[mis] = key.Length - i - 1;
                    mis++;
                    j += uLen;
                }
            }
        }


        /// <summary>
        /// Returns powers of 58 multiplied by <paramref name="maxPow"/> then shifts them left so that it doesn't need it later
        /// when converting to SHA256 working vector
        /// <para/>0*58^0 0*58^1 ... 0*58^<paramref name="maxPow"/> 1*58^0 ...
        /// </summary>
        public static ulong[] GetShiftedMultPow58(int maxPow, int uLen, int shift)
        {
            Debug.Assert(shift <= 24 && shift >= 0);

            var padded = new byte[4 * uLen];
            var multPow = new ulong[maxPow * uLen * 58];
            for (int i = 0, pindex = 0; i < 58; i++)
            {
                for (int j = 0; j < maxPow; j++)
                {
                    BigInteger val = BigInteger.Pow(58, j) * i;
                    byte[] temp = val.ToByteArrayExt(false, true);

                    Array.Clear(padded, 0, padded.Length);
                    Buffer.BlockCopy(temp, 0, padded, 0, temp.Length);

                    for (int k = 0; k < padded.Length; pindex++, k += 4)
                    {
                        multPow[pindex] =
                            (uint)(padded[k] << 0 | padded[k + 1] << 8 | padded[k + 2] << 16 | padded[k + 3] << 24);
                        multPow[pindex] <<= shift;
                    }
                }
            }
            return multPow;
        }

        public void InitializeCompressWif(ReadOnlySpan<char> key, char missingChar)
        {
            const int uLen = 10; // Maximum result (58^52) is 39 bytes = 39/4 = 10 uint

            multPow58 = GetShiftedMultPow58(ConstantsFO.PrivKeyCompWifLen, uLen, 16);
            preC = new ulong[uLen];

            // calculate what we already have and store missing indexes
            int mis = 0;
            for (int i = key.Length - 1, j = 0; i >= 0; i--)
            {
                if (key[i] != missingChar)
                {
                    int index = ConstantsFO.Base58Chars.IndexOf(key[i]);
                    int chunk = (index * 520) + (uLen * (key.Length - 1 - i));
                    ulong carry = 0;
                    for (int k = uLen - 1; k >= 0; k--, j++)
                    {
                        preC[k] += multPow58[k + chunk] + carry;
                    }
                }
                else
                {
                    missingIndexes[mis] = key.Length - i - 1;
                    mis++;
                    j += uLen;
                }
            }
        }

        public void InitializeUncompressWif(ReadOnlySpan<char> key, char missingChar)
        {
            const int uLen = 10;

            multPow58 = GetShiftedMultPow58(ConstantsFO.PrivKeyUncompWifLen, uLen, 24);
            preC = new ulong[uLen];

            // calculate what we already have and store missing indexes
            int mis = 0;
            for (int i = key.Length - 1, j = 0; i >= 0; i--)
            {
                if (key[i] != missingChar)
                {
                    int index = ConstantsFO.Base58Chars.IndexOf(key[i]);
                    int chunk = (index * key.Length * 10) + (uLen * (key.Length - 1 - i));
                    ulong carry = 0;
                    for (int k = uLen - 1; k >= 0; k--, j++)
                    {
                        preC[k] += multPow58[k + chunk] + carry;
                    }
                }
                else
                {
                    missingIndexes[mis] = key.Length - i - 1;
                    mis++;
                    j += uLen;
                }
            }
        }


        private static BigInteger GetTotalCount(int missCount) => BigInteger.Pow(58, missCount);

        private bool IsMissingFromEnd()
        {
            if (missingIndexes[0] != 0)
            {
                return false;
            }

            if (missingIndexes.Length != 1)
            {
                for (int i = 1; i < missingIndexes.Length; i++)
                {
                    if (missingIndexes[i] - missingIndexes[i - 1] != 1)
                    {
                        return false;
                    }
                }
            }
            return true;
        }


        private const int WifEndDiv = 1_000_000;
        private bool isWifEndCompressed;
        private BigInteger wifEndStart;
        private void SetResultParallelWifEnd(int added)
        {
            using PrivateKey tempKey = new(wifEndStart + added);
            string tempWif = tempKey.ToWif(isWifEndCompressed);
            report.AddMessageSafe($"Found the key: {tempWif}");
            report.FoundAnyResult = true;
        }
        private void WifLoopMissingEnd(in Scalar smallKey, int start, long max,
                                       ICompareService comparer, ParallelLoopState loopState)
        {
            if (loopState.IsStopped)
            {
                return;
            }

            var calc2 = new Calc();
            var toAddSc = new Scalar((uint)(start * WifEndDiv), 0, 0, 0, 0, 0, 0, 0);
            Scalar initial = smallKey.Add(toAddSc, out int overflow);
            if (overflow != 0)
            {
                return;
            }
            PointJacobian pt = calc2.MultiplyByG(initial);
            Point g = Calc.G;

            for (int i = 0; i < max; i++)
            {
                // The first point is the smallKey * G the next is smallKey+1 * G
                // And there is one extra addition at the end which shouldn't matter speed-wise
                if (comparer.Compare(pt))
                {
                    SetResultParallelWifEnd((start * WifEndDiv) + i);

                    loopState.Stop();
                    break;
                }
                pt = pt.AddVariable(g);
            }

            report.IncrementProgress();
        }

        private void WifLoopMissingEnd(bool compressed)
        {
            // Numbers are approximates, values usually are ±1
            //         Uncompressed ;     Compressed
            // 1-5 ->             1 ;              1
            // 6   ->             9 ;              1
            // 7   ->           514 ;              3
            // 8   ->        29,817 ;            117
            // 9   ->     1,729,387 ;          6,756
            // 10  ->   100,304,420 ;        391,815
            // 11  -> 5,817,656,406 ;     22,725,222  <-- FinderOuter limits the search to 11
            // 12  ->               ;  1,318,062,780

            string baseWif = keyToCheck.Substring(0, keyToCheck.Length - missCount);
            string smallWif = $"{baseWif}{new string(Enumerable.Repeat(ConstantsFO.Base58Chars[0], missCount).ToArray())}";
            string bigWif = $"{baseWif}{new string(Enumerable.Repeat(ConstantsFO.Base58Chars[^1], missCount).ToArray())}";
            var start = Base58.Decode(smallWif).SubArray(1, 32).ToBigInt(true, true);
            var end = Base58.Decode(bigWif).SubArray(1, 32).ToBigInt(true, true);

            // If the key (integer) value is so tiny that almost all of its higher bytes are zero, or too big that almost
            // all of its bytes are 0xff the smallWif string can end up being bigger in value than the bigWif string 
            // and in some cases withwith an invalid first byte.
            // Chances of a wallet producing such values is practically zero, so the following condition is only
            // to prevent program from crashing if someone used a _test_ key!
            if (end < start)
            {
                report.AddMessageSafe($"The given key is an edge case that can not be recovered. If this key was created by " +
                                      $"a wallet and not some puzzle or test,... please open an issue on GitHub." +
                                      $"{Environment.NewLine}" +
                                      $"Here are the upper and lower values of the given key (DO NOT SHARE THESE):" +
                                      $"{Environment.NewLine}" +
                                      $"Low:{Environment.NewLine}    {smallWif}{Environment.NewLine}" +
                                      $"    {Base58.Decode(smallWif).ToBase16()}" +
                                      $"{Environment.NewLine}" +
                                      $"High:{Environment.NewLine}    {bigWif}{Environment.NewLine}" +
                                      $"    {Base58.Decode(bigWif).ToBase16()}");
                return;
            }

            var diff = end - start + 1;
            report.AddMessageSafe($"Using an optimized method checking only {diff:n0} keys.");

            var curve = new SecP256k1();
            if (start == 0 || end >= curve.N)
            {
                report.AddMessageSafe("There is something wrong with the given key, it is outside of valid key range.");
                return;
            }

            // With small number of missing keys there is only 1 result or worse case 2 which is simply printed without
            // needing ICompareService. Instead all possible addresses are printed.
            if (diff < 3)
            {
                for (int i = 0; i < (int)diff; i++)
                {
                    using PrivateKey tempKey = new(start + i);
                    string tempWif = tempKey.ToWif(compressed);
                    if (tempWif.Contains(baseWif))
                    {
                        var pub = tempKey.ToPublicKey();
                        string msg = $"Found the key: {tempWif}{Environment.NewLine}" +
                            $"     Compressed P2PKH address={Address.GetP2pkh(pub, true)}{Environment.NewLine}" +
                            $"     Uncompressed P2PKH address={Address.GetP2pkh(pub, false)}{Environment.NewLine}" +
                            $"     Compressed P2WPKH address={Address.GetP2wpkh(pub, 0)}{Environment.NewLine}" +
                            $"     Compressed P2SH-P2WPKH address={Address.GetP2sh_P2wpkh(pub, 0)}";
                        report.AddMessageSafe(msg);
                        report.FoundAnyResult = true;
                    }
                }

                return;
            }

            if (comparer is null)
            {
                report.AddMessageSafe("You must enter address or pubkey to compare with results.");
                return;
            }

            // TODO: this part could run in parallel ICompareService is instantiated here for each thread.
            var calc = new ECCalc();
            EllipticCurvePoint point = calc.MultiplyByG(start);

            var calc2 = new Calc();
            var sc = new Scalar(Base58.Decode(smallWif).SubArray(1, 32), out int overflow);

            isWifEndCompressed = compressed;
            wifEndStart = start;

            int loopLastMax = (int)((long)diff % WifEndDiv);
            int loopCount = (int)((long)diff / WifEndDiv) + (loopLastMax == 0 ? 0 : 1);

            report.AddMessageSafe("Running in parallel.");
            report.SetProgressStep(loopCount);

            Parallel.For(0, loopCount, (i, state) =>
                             WifLoopMissingEnd(sc, i, i == loopCount - 1 ? loopLastMax : WifEndDiv, comparer.Clone(), state));
        }


        private void SetResultParallel(uint[] missingItems, int firstItem)
        {
            // Chances of finding more than 1 correct result is very small in base-58 and even if it happened 
            // this method would be called in very long intervals, meaning UI updates here are not an issue.
            report.AddMessageSafe($"Found a possible result (will continue checking the rest):");

            char[] temp = keyToCheck.ToCharArray();
            int i = 0;
            if (firstItem != -1)
            {
                temp[temp.Length - missingIndexes[i++] - 1] = ConstantsFO.Base58Chars[firstItem];
            }
            foreach (var index in missingItems)
            {
                temp[temp.Length - missingIndexes[i++] - 1] = ConstantsFO.Base58Chars[(int)index];
            }

            report.AddMessageSafe(new string(temp));
            report.FoundAnyResult = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool MoveNext(uint* cartesian, int len)
        {
            for (int i = len - 1; i >= 0; --i)
            {
                cartesian[i] += 1;

                if (cartesian[i] == 58)
                {
                    cartesian[i] = 0;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        private unsafe void LoopComp(ulong[] precomputed, int firstItem, int misStart, uint[] missingItems)
        {
            ulong* tmp = stackalloc ulong[precomputed.Length];
            uint* pt = stackalloc uint[Sha256Fo.UBufferSize];
            fixed (ulong* pow = &multPow58[0], pre = &precomputed[0])
            fixed (uint* itemsPt = &missingItems[0])
            fixed (int* mi = &missingIndexes[misStart])
            {
                do
                {
                    Buffer.MemoryCopy(pre, tmp, 80, 80);
                    int i = 0;
                    foreach (int keyItem in missingItems)
                    {
                        int chunk = (keyItem * 520) + (10 * mi[i++]);

                        tmp[0] += pow[0 + chunk];
                        tmp[1] += pow[1 + chunk];
                        tmp[2] += pow[2 + chunk];
                        tmp[3] += pow[3 + chunk];
                        tmp[4] += pow[4 + chunk];
                        tmp[5] += pow[5 + chunk];
                        tmp[6] += pow[6 + chunk];
                        tmp[7] += pow[7 + chunk];
                        tmp[8] += pow[8 + chunk];
                        tmp[9] += pow[9 + chunk];
                    }

                    // Normalize:
                    tmp[1] += tmp[0] >> 32;
                    pt[16] = ((uint)tmp[1] & 0xffff0000) | 0b00000000_00000000_10000000_00000000U; tmp[2] += tmp[1] >> 32;
                    pt[15] = (uint)tmp[2]; tmp[3] += tmp[2] >> 32;
                    pt[14] = (uint)tmp[3]; tmp[4] += tmp[3] >> 32;
                    pt[13] = (uint)tmp[4]; tmp[5] += tmp[4] >> 32;
                    pt[12] = (uint)tmp[5]; tmp[6] += tmp[5] >> 32;
                    pt[11] = (uint)tmp[6]; tmp[7] += tmp[6] >> 32;
                    pt[10] = (uint)tmp[7]; tmp[8] += tmp[7] >> 32;
                    pt[9] = (uint)tmp[8]; tmp[9] += tmp[8] >> 32;
                    pt[8] = (uint)tmp[9];
                    Debug.Assert(tmp[9] >> 32 == 0);

                    if (((pt[8] & 0xff000000) | (pt[16] & 0x00ff0000)) == 0x80010000)
                    {
                        uint expectedCS = (uint)tmp[0] >> 16 | (uint)tmp[1] << 16;

                        // The following has to be set since second block compression changes it
                        pt[23] = 272; // 34 *8 = 272
                        Sha256Fo.Init(pt);
                        Sha256Fo.CompressDouble34(pt);

                        if (pt[0] == expectedCS)
                        {
                            SetResultParallel(missingItems, firstItem);
                        }
                    }
                } while (MoveNext(itemsPt, missingItems.Length));
            }

            report.IncrementProgress();
        }
        private unsafe ulong[] ParallelPre(int firstItem, int len)
        {
            ulong[] localPre = new ulong[preC.Length];
            fixed (ulong* lpre = &localPre[0], pre = &preC[0], pow = &multPow58[0])
            {
                Buffer.MemoryCopy(pre, lpre, 80, 80);
                int index = missingIndexes[0];
                int chunk = (firstItem * len * 10) + (10 * index);

                lpre[0] += pow[0 + chunk];
                lpre[1] += pow[1 + chunk];
                lpre[2] += pow[2 + chunk];
                lpre[3] += pow[3 + chunk];
                lpre[4] += pow[4 + chunk];
                lpre[5] += pow[5 + chunk];
                lpre[6] += pow[6 + chunk];
                lpre[7] += pow[7 + chunk];
                lpre[8] += pow[8 + chunk];
                lpre[9] += pow[9 + chunk];
            }

            return localPre;
        }
        private unsafe void LoopComp()
        {
            if (IsMissingFromEnd() && missCount <= 11)
            {
                WifLoopMissingEnd(true);
            }
            else if (missCount >= 5)
            {
                // 4 missing chars is 11,316,496 cases and it takes <2 seconds to run.
                // That makes 5 the optimal number for using parallelization
                report.SetProgressStep(58);
                report.AddMessageSafe("Running in parallel.");
                Parallel.For(0, 58, (firstItem) => LoopComp(ParallelPre(firstItem, 52), firstItem, 1, new uint[missCount - 1]));
            }
            else
            {
                LoopComp(preC, -1, 0, new uint[missCount]);
            }
        }

        private unsafe void LoopUncomp(ulong[] precomputed, int firstItem, int misStart, uint[] missingItems)
        {
            ulong* tmp = stackalloc ulong[precomputed.Length];
            uint* pt = stackalloc uint[Sha256Fo.UBufferSize];
            fixed (ulong* pow = &multPow58[0], pre = &precomputed[0])
            fixed (uint* itemsPt = &missingItems[0])
            fixed (int* mi = &missingIndexes[misStart])
            {
                do
                {
                    Buffer.MemoryCopy(pre, tmp, 80, 80);
                    int i = 0;
                    foreach (int keyItem in missingItems)
                    {
                        int chunk = (keyItem * 510) + (10 * mi[i++]);

                        tmp[0] += pow[0 + chunk];
                        tmp[1] += pow[1 + chunk];
                        tmp[2] += pow[2 + chunk];
                        tmp[3] += pow[3 + chunk];
                        tmp[4] += pow[4 + chunk];
                        tmp[5] += pow[5 + chunk];
                        tmp[6] += pow[6 + chunk];
                        tmp[7] += pow[7 + chunk];
                        tmp[8] += pow[8 + chunk];
                        tmp[9] += pow[9 + chunk];
                    }

                    // Normalize:
                    tmp[1] += tmp[0] >> 32;
                    pt[16] = ((uint)tmp[1] & 0xff000000) | 0b00000000_10000000_00000000_00000000U; tmp[2] += tmp[1] >> 32;
                    pt[15] = (uint)tmp[2]; tmp[3] += tmp[2] >> 32;
                    pt[14] = (uint)tmp[3]; tmp[4] += tmp[3] >> 32;
                    pt[13] = (uint)tmp[4]; tmp[5] += tmp[4] >> 32;
                    pt[12] = (uint)tmp[5]; tmp[6] += tmp[5] >> 32;
                    pt[11] = (uint)tmp[6]; tmp[7] += tmp[6] >> 32;
                    pt[10] = (uint)tmp[7]; tmp[8] += tmp[7] >> 32;
                    pt[9] = (uint)tmp[8]; tmp[9] += tmp[8] >> 32;
                    pt[8] = (uint)tmp[9];
                    Debug.Assert(tmp[9] >> 32 == 0);

                    if ((pt[8] & 0xff000000) == 0x80000000)
                    {
                        uint expectedCS = (uint)tmp[0] >> 24 | (uint)tmp[1] << 8;

                        // The following has to be set since second block compression changes it
                        pt[23] = 264; // 33 *8 = 264

                        Sha256Fo.Init(pt);
                        Sha256Fo.CompressDouble33(pt);

                        if (pt[0] == expectedCS)
                        {
                            SetResultParallel(missingItems, firstItem);
                        }
                    }
                } while (MoveNext(itemsPt, missingItems.Length));
            }

            report.IncrementProgress();
        }
        private unsafe void LoopUncomp()
        {
            if (IsMissingFromEnd() && missCount <= 11)
            {
                WifLoopMissingEnd(false);
            }
            else if (missCount >= 5)
            {
                // Same as LoopComp()
                report.SetProgressStep(58);
                report.AddMessageSafe("Running in parallel.");
                Parallel.For(0, 58, (firstItem) => LoopUncomp(ParallelPre(firstItem, 51), firstItem, 1, new uint[missCount - 1]));
            }
            else
            {
                LoopUncomp(preC, -1, 0, new uint[missCount]);
            }
        }


        private unsafe void Loop21(uint[] precomputed, int firstItem, int misStart, uint[] missingItems)
        {
            uint[] temp = new uint[precomputed.Length];
            uint* pt = stackalloc uint[Sha256Fo.UBufferSize];
            fixed (uint* pow = &powers58[0], pre = &precomputed[0], tmp = &temp[0])
            fixed (uint* itemsPt = &missingItems[0])
            fixed (int* mi = &missingIndexes[misStart])
            {
                do
                {
                    Buffer.MemoryCopy(pre, tmp, 28, 28);
                    int i = 0;
                    foreach (var keyItem in missingItems)
                    {
                        ulong carry = 0;
                        for (int k = 6, j = 0; k >= 0; k--, j++)
                        {
                            ulong result = (pow[(mi[i] * 7) + j] * (ulong)keyItem) + tmp[k] + carry;
                            tmp[k] = (uint)result;
                            carry = (uint)(result >> 32);
                        }
                        i++;
                    }

                    pt[8] = (tmp[0] << 24) | (tmp[1] >> 8);
                    pt[9] = (tmp[1] << 24) | (tmp[2] >> 8);
                    pt[10] = (tmp[2] << 24) | (tmp[3] >> 8);
                    pt[11] = (tmp[3] << 24) | (tmp[4] >> 8);
                    pt[12] = (tmp[4] << 24) | (tmp[5] >> 8);
                    pt[13] = (tmp[5] << 24) | 0b00000000_10000000_00000000_00000000U;
                    pt[14] = 0;
                    pt[15] = 0;
                    pt[16] = 0;
                    // from 6 to 14 = 0
                    pt[23] = 168; // 21 *8 = 168

                    Sha256Fo.Init(pt);
                    Sha256Fo.CompressDouble21(pt);

                    if (pt[0] == tmp[6])
                    {
                        SetResultParallel(missingItems, firstItem);
                    }
                } while (MoveNext(itemsPt, missingItems.Length));
            }

            report.IncrementProgress();
        }
        private unsafe uint[] ParallelPre21(int firstItem)
        {
            uint[] localPre = new uint[precomputed.Length];
            fixed (uint* lpre = &localPre[0], pre = &precomputed[0], pow = &powers58[0])
            {
                Buffer.MemoryCopy(pre, lpre, 28, 28);
                int index = missingIndexes[0];
                ulong carry = 0;
                for (int k = 6, j = 0; k >= 0; k--, j++)
                {
                    ulong result = (pow[(index * 7) + j] * (ulong)firstItem) + lpre[k] + carry;
                    lpre[k] = (uint)result;
                    carry = (uint)(result >> 32);
                }
            }

            return localPre;
        }
        private unsafe void Loop21()
        {
            if (missCount >= 5)
            {
                report.SetProgressStep(58);
                report.AddMessageSafe("Running in parallel.");
                Parallel.For(0, 58, (firstItem) => Loop21(ParallelPre21(firstItem), firstItem, 1, new uint[missCount - 1]));
            }
            else
            {
                Loop21(precomputed, -1, 0, new uint[missCount]);
            }
        }


        private unsafe void Loop58(uint[] precomputed, int firstItem, int misStart, uint[] missingItems)
        {
            uint[] temp = new uint[precomputed.Length];
            uint* pt = stackalloc uint[Sha256Fo.UBufferSize];
            fixed (uint* pow = &powers58[0], pre = &precomputed[0], tmp = &temp[0])
            fixed (uint* itemsPt = &missingItems[0])
            fixed (int* mi = &missingIndexes[misStart])
            {
                do
                {
                    Buffer.MemoryCopy(pre, tmp, 44, 44);
                    int i = 0;
                    foreach (var keyItem in missingItems)
                    {
                        ulong carry = 0;
                        for (int k = 10, j = 0; k >= 0; k--, j++)
                        {
                            ulong result = (pow[(mi[i] * 11) + j] * (ulong)keyItem) + tmp[k] + carry;
                            tmp[k] = (uint)result;
                            carry = (uint)(result >> 32);
                        }
                        i++;
                    }

                    pt[8] = (tmp[0] << 8) | (tmp[1] >> 24);
                    pt[9] = (tmp[1] << 8) | (tmp[2] >> 24);
                    pt[10] = (tmp[2] << 8) | (tmp[3] >> 24);
                    pt[11] = (tmp[3] << 8) | (tmp[4] >> 24);
                    pt[12] = (tmp[4] << 8) | (tmp[5] >> 24);
                    pt[13] = (tmp[5] << 8) | (tmp[6] >> 24);
                    pt[14] = (tmp[6] << 8) | (tmp[7] >> 24);
                    pt[15] = (tmp[7] << 8) | (tmp[8] >> 24);
                    pt[16] = (tmp[8] << 8) | (tmp[9] >> 24);
                    pt[17] = (tmp[9] << 8) | 0b00000000_00000000_00000000_10000000U;
                    // from 10 to 14 = 0
                    pt[23] = 312; // 39 *8 = 168

                    Sha256Fo.Init(pt);
                    Sha256Fo.CompressDouble39(pt);

                    if (pt[0] == tmp[10])
                    {
                        SetResultParallel(missingItems, firstItem);
                    }
                } while (MoveNext(itemsPt, missingItems.Length));
            }

            report.IncrementProgress();
        }
        private unsafe uint[] ParallelPre58(int firstItem)
        {
            uint[] localPre = new uint[precomputed.Length];
            fixed (uint* lpre = &localPre[0], pre = &precomputed[0], pow = &powers58[0])
            {
                Buffer.MemoryCopy(pre, lpre, 44, 44);
                int index = missingIndexes[0];
                ulong carry = 0;
                for (int k = 10, j = 0; k >= 0; k--, j++)
                {
                    ulong result = (pow[(index * 11) + j] * (ulong)firstItem) + lpre[k] + carry;
                    lpre[k] = (uint)result;
                    carry = (uint)(result >> 32);
                }
            }

            return localPre;
        }
        private unsafe void Loop58()
        {
            if (missCount >= 5)
            {
                report.SetProgressStep(58);
                report.AddMessageSafe("Running in parallel.");
                Parallel.For(0, 58, (firstItem) => Loop58(ParallelPre58(firstItem), firstItem, 1, new uint[missCount - 1]));
            }
            else
            {
                Loop58(precomputed, -1, 0, new uint[missCount]);
            }
        }



        private unsafe bool SpecialLoopComp1(string key, bool comp)
        {
            int maxKeyLen = comp ? ConstantsFO.PrivKeyCompWifLen : ConstantsFO.PrivKeyUncompWifLen;

            byte[] padded;
            int uLen;

            // Maximum result (58^52) is 39 bytes = 39/4 = 10 uint
            uLen = 10;
            uint[] powers58 = new uint[maxKeyLen * uLen];
            padded = new byte[4 * uLen];

            for (int i = 0, j = 0; i < maxKeyLen; i++)
            {
                BigInteger val = BigInteger.Pow(58, i);
                byte[] temp = val.ToByteArray(true, false);

                Array.Clear(padded, 0, padded.Length);
                Buffer.BlockCopy(temp, 0, padded, 0, temp.Length);

                for (int k = 0; k < padded.Length; j++, k += 4)
                {
                    powers58[j] = (uint)(padded[k] << 0 | padded[k + 1] << 8 | padded[k + 2] << 16 | padded[k + 3] << 24);
                }
            }

            int[] values = new int[key.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ConstantsFO.Base58Chars.IndexOf(key[i]);
            }

            Span<uint> precomputed = new uint[uLen];

            fixed (uint* pre = &precomputed[0], pow = &powers58[0])
            {
                // i starts from 1 becaue it is compressed (K or L)
                for (int i = 1; i < maxKeyLen; i++)
                {
                    precomputed.Clear();

                    for (int index = 0; index < i; index++)
                    {
                        ulong carry = 0;
                        ulong val = (ulong)values[index];
                        int powIndex = (maxKeyLen - 1 - index) * uLen;
                        for (int m = uLen - 1; m >= 0; m--, powIndex++)
                        {
                            ulong result = (pow[powIndex] * val) + pre[m] + carry;
                            pre[m] = (uint)result;
                            carry = (uint)(result >> 32);
                        }
                    }

                    for (int index = i + 1; index < maxKeyLen; index++)
                    {
                        ulong carry = 0;
                        ulong val = (ulong)values[index - 1];
                        int powIndex = (maxKeyLen - 1 - index) * uLen;
                        for (int m = uLen - 1; m >= 0; m--, powIndex++)
                        {
                            ulong result = (pow[powIndex] * val) + pre[m] + carry;
                            pre[m] = (uint)result;
                            carry = (uint)(result >> 32);
                        }
                    }

                    for (int c1 = 0; c1 < 58; c1++)
                    {
                        Span<uint> temp = new uint[precomputed.Length];
                        precomputed.CopyTo(temp);

                        ulong carry = 0;
                        ulong val = (ulong)c1;
                        int powIndex = (maxKeyLen - 1 - i) * uLen;
                        for (int m = uLen - 1; m >= 0; m--, powIndex++)
                        {
                            ulong result = (powers58[powIndex] * val) + temp[m] + carry;
                            temp[m] = (uint)result;
                            carry = (uint)(result >> 32);
                        }

                        bool checksum = comp ? ComputeSpecialCompHash(temp) : ComputeSpecialUncompHash(temp);
                        if (checksum)
                        {
                            string foundRes = key.Insert(i, $"{ConstantsFO.Base58Chars[c1]}");
                            report.AddMessageSafe($"Found a key: {foundRes}");
                            report.FoundAnyResult = true;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private unsafe bool SpecialLoopComp2(string key, bool comp)
        {
            int maxKeyLen = comp ? ConstantsFO.PrivKeyCompWifLen : ConstantsFO.PrivKeyUncompWifLen;

            byte[] padded;

            // Maximum result (58^52) is 39 bytes = 39/4 = 10 uint
            const int uLen = 10;
            uint[] powers58 = new uint[maxKeyLen * uLen];
            padded = new byte[4 * uLen];

            for (int i = 0, j = 0; i < maxKeyLen; i++)
            {
                BigInteger val = BigInteger.Pow(58, i);
                byte[] temp = val.ToByteArray(true, false);

                Array.Clear(padded, 0, padded.Length);
                Buffer.BlockCopy(temp, 0, padded, 0, temp.Length);

                for (int k = 0; k < padded.Length; j++, k += 4)
                {
                    powers58[j] = (uint)(padded[k] << 0 | padded[k + 1] << 8 | padded[k + 2] << 16 | padded[k + 3] << 24);
                }
            }

            int[] values = new int[key.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ConstantsFO.Base58Chars.IndexOf(key[i]);
            }

            uint[] precomputed = new uint[uLen];
            fixed (uint* pre = &precomputed[0], pow = &powers58[0])
            {
                // i starts from 1 becaue it is compressed (K or L)
                for (int i = 1; i < maxKeyLen - 1; i++)
                {
                    for (int j = i + 1; j < maxKeyLen; j++)
                    {
                        ((Span<uint>)precomputed).Clear();

                        for (int index = 0; index < i; index++)
                        {
                            ulong carry = 0;
                            ulong val = (ulong)values[index];
                            int powIndex = (maxKeyLen - 1 - index) * uLen;
                            for (int m = uLen - 1; m >= 0; m--, powIndex++)
                            {
                                ulong result = (pow[powIndex] * val) + pre[m] + carry;
                                pre[m] = (uint)result;
                                carry = (uint)(result >> 32);
                            }
                        }

                        for (int index = i + 1; index < j; index++)
                        {
                            ulong carry = 0;
                            ulong val = (ulong)values[index - 1];
                            int powIndex = (maxKeyLen - 1 - index) * uLen;
                            for (int m = uLen - 1; m >= 0; m--, powIndex++)
                            {
                                ulong result = (pow[powIndex] * val) + pre[m] + carry;
                                pre[m] = (uint)result;
                                carry = (uint)(result >> 32);
                            }
                        }

                        for (int index = j + 1; index < maxKeyLen; index++)
                        {
                            ulong carry = 0;
                            ulong val = (ulong)values[index - 2];
                            int powIndex = (maxKeyLen - 1 - index) * uLen;
                            for (int m = uLen - 1; m >= 0; m--, powIndex++)
                            {
                                ulong result = (pow[powIndex] * val) + pre[m] + carry;
                                pre[m] = (uint)result;
                                carry = (uint)(result >> 32);
                            }
                        }

                        Debug.Assert(pow[0] == 12);

                        Parallel.For(0, 58, (c1, state) =>
                        {
                            for (int c2 = 0; c2 < 58; c2++)
                            {
                                if (state.IsStopped)
                                {
                                    return;
                                }

                                Span<uint> temp = new uint[precomputed.Length];
                                precomputed.CopyTo(temp);

                                ulong carry = 0;
                                ulong val = (ulong)c1;
                                int powIndex = (maxKeyLen - 1 - i) * uLen;
                                for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                {
                                    ulong result = (powers58[powIndex] * val) + temp[m] + carry;
                                    temp[m] = (uint)result;
                                    carry = (uint)(result >> 32);
                                }

                                carry = 0;
                                val = (ulong)c2;
                                powIndex = (maxKeyLen - 1 - j) * uLen;
                                for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                {
                                    ulong result = (powers58[powIndex] * val) + temp[m] + carry;
                                    temp[m] = (uint)result;
                                    carry = (uint)(result >> 32);
                                }

                                bool checksum = comp ? ComputeSpecialCompHash(temp) : ComputeSpecialUncompHash(temp);
                                if (checksum)
                                {
                                    string foundRes = key.Insert(i, $"{ConstantsFO.Base58Chars[c1]}")
                                                         .Insert(j, $"{ConstantsFO.Base58Chars[c2]}");
                                    report.AddMessageSafe($"Found a key: {foundRes}");
                                    report.FoundAnyResult = true;
                                    state.Stop();
                                    return;
                                }
                            }
                        });
                    }
                }
            }
            return false;
        }

        private unsafe bool SpecialLoopComp3(string key)
        {
            byte[] padded;
            int uLen;

            // Maximum result (58^52) is 39 bytes = 39/4 = 10 uint
            uLen = 10;
            uint[] powers58 = new uint[ConstantsFO.PrivKeyCompWifLen * uLen];
            padded = new byte[4 * uLen];

            for (int i = 0, j = 0; i < ConstantsFO.PrivKeyCompWifLen; i++)
            {
                BigInteger val = BigInteger.Pow(58, i);
                byte[] temp = val.ToByteArray(true, false);

                Array.Clear(padded, 0, padded.Length);
                Buffer.BlockCopy(temp, 0, padded, 0, temp.Length);

                for (int k = 0; k < padded.Length; j++, k += 4)
                {
                    powers58[j] = (uint)(padded[k] << 0 | padded[k + 1] << 8 | padded[k + 2] << 16 | padded[k + 3] << 24);
                }
            }

            int[] values = new int[key.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ConstantsFO.Base58Chars.IndexOf(key[i]);
            }

            uint[] precomputed = new uint[uLen];

            fixed (uint* pre = &precomputed[0], pow = &powers58[0])
            {
                // i starts from 1 becaue it is compressed (K or L)
                for (int i = 1; i < ConstantsFO.PrivKeyCompWifLen - 2; i++)
                {
                    for (int j = i + 1; j < ConstantsFO.PrivKeyCompWifLen - 1; j++)
                    {
                        for (int k = j + 1; k < ConstantsFO.PrivKeyCompWifLen; k++)
                        {
                            ((Span<uint>)precomputed).Clear();

                            for (int index = 0; index < i; index++)
                            {
                                ulong carry = 0;
                                ulong val = (ulong)values[index];
                                int powIndex = (ConstantsFO.PrivKeyCompWifLen - 1 - index) * uLen;
                                for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                {
                                    ulong result = (pow[powIndex] * val) + pre[m] + carry;
                                    pre[m] = (uint)result;
                                    carry = (uint)(result >> 32);
                                }
                            }

                            for (int index = i + 1; index < j; index++)
                            {
                                ulong carry = 0;
                                ulong val = (ulong)values[index - 1];
                                int powIndex = (ConstantsFO.PrivKeyCompWifLen - 1 - index) * uLen;
                                for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                {
                                    ulong result = (pow[powIndex] * val) + pre[m] + carry;
                                    pre[m] = (uint)result;
                                    carry = (uint)(result >> 32);
                                }
                            }

                            for (int index = j + 1; index < k; index++)
                            {
                                ulong carry = 0;
                                ulong val = (ulong)values[index - 2];
                                int powIndex = (ConstantsFO.PrivKeyCompWifLen - 1 - index) * uLen;
                                for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                {
                                    ulong result = (pow[powIndex] * val) + pre[m] + carry;
                                    pre[m] = (uint)result;
                                    carry = (uint)(result >> 32);
                                }
                            }

                            for (int index = k + 1; index < ConstantsFO.PrivKeyCompWifLen; index++)
                            {
                                ulong carry = 0;
                                ulong val = (ulong)values[index - 3];
                                int powIndex = (ConstantsFO.PrivKeyCompWifLen - 1 - index) * uLen;
                                for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                {
                                    ulong result = (pow[powIndex] * val) + pre[m] + carry;
                                    pre[m] = (uint)result;
                                    carry = (uint)(result >> 32);
                                }
                            }

                            var cancelToken = new CancellationTokenSource();
                            var options = new ParallelOptions
                            {
                                CancellationToken = cancelToken.Token,
                            };

                            try
                            {
                                Parallel.For(0, 58, options, (c1, loopState) =>
                                {
                                    for (int c2 = 0; c2 < 58; c2++)
                                    {
                                        for (int c3 = 0; c3 < 58; c3++)
                                        {
                                            options.CancellationToken.ThrowIfCancellationRequested();

                                            Span<uint> temp = new uint[uLen];
                                            ((ReadOnlySpan<uint>)precomputed).CopyTo(temp);

                                            ulong carry = 0;
                                            ulong val = (ulong)c1;
                                            int powIndex = (ConstantsFO.PrivKeyCompWifLen - 1 - i) * uLen;
                                            for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                            {
                                                ulong result = (powers58[powIndex] * val) + temp[m] + carry;
                                                temp[m] = (uint)result;
                                                carry = (uint)(result >> 32);
                                            }

                                            carry = 0;
                                            val = (ulong)c2;
                                            powIndex = (ConstantsFO.PrivKeyCompWifLen - 1 - j) * uLen;
                                            for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                            {
                                                ulong result = (powers58[powIndex] * val) + temp[m] + carry;
                                                temp[m] = (uint)result;
                                                carry = (uint)(result >> 32);
                                            }

                                            carry = 0;
                                            val = (ulong)c3;
                                            powIndex = (ConstantsFO.PrivKeyCompWifLen - 1 - k) * uLen;
                                            for (int m = uLen - 1; m >= 0; m--, powIndex++)
                                            {
                                                ulong result = (powers58[powIndex] * val) + temp[m] + carry;
                                                temp[m] = (uint)result;
                                                carry = (uint)(result >> 32);
                                            }

                                            if (ComputeSpecialCompHash(temp))
                                            {
                                                string foundRes = key.Insert(i, $"{ConstantsFO.Base58Chars[c1]}")
                                                                     .Insert(j, $"{ConstantsFO.Base58Chars[c2]}")
                                                                     .Insert(k, $"{ConstantsFO.Base58Chars[c3]}");
                                                report.AddMessageSafe($"Found a key: {foundRes}");
                                                //Task.Run(() => cancelToken.Cancel());
                                                report.FoundAnyResult = true;
                                            }
                                        }
                                    }
                                });
                            }
                            catch (Exception)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private static unsafe bool ComputeSpecialUncompHash(Span<uint> keyValueInts)
        {
            if (keyValueInts[0] != 0x00000080)
            {
                return false;
            }

            uint* pt = stackalloc uint[Sha256Fo.UBufferSize];
            fixed (uint* keyPt = &keyValueInts[0])
            {
                pt[8] = (keyPt[0] << 24) | (keyPt[1] >> 8);
                pt[9] = (keyPt[1] << 24) | (keyPt[2] >> 8);
                pt[10] = (keyPt[2] << 24) | (keyPt[3] >> 8);
                pt[11] = (keyPt[3] << 24) | (keyPt[4] >> 8);
                pt[12] = (keyPt[4] << 24) | (keyPt[5] >> 8);
                pt[13] = (keyPt[5] << 24) | (keyPt[6] >> 8);
                pt[14] = (keyPt[6] << 24) | (keyPt[7] >> 8);
                pt[15] = (keyPt[7] << 24) | (keyPt[8] >> 8);
                pt[16] = (keyPt[8] << 24) | 0b00000000_10000000_00000000_00000000U;
                // from 9 to 14 = 0
                pt[23] = 264; // 33 *8 = 264

                Sha256Fo.Init(pt);
                Sha256Fo.CompressDouble33(pt);

                return pt[0] == keyPt[9];
            }
        }

        private static unsafe bool ComputeSpecialCompHash(Span<uint> keyValueInts)
        {
            if (((keyValueInts[0] & 0xffffff00) | (keyValueInts[^2] & 0x000000ff)) != 0x00008001)
            {
                return false;
            }

            uint* pt = stackalloc uint[Sha256Fo.UBufferSize];
            fixed (uint* keyPt = &keyValueInts[0])
            {
                pt[8] = (keyPt[0] << 16) | (keyPt[1] >> 16);
                pt[9] = (keyPt[1] << 16) | (keyPt[2] >> 16);
                pt[10] = (keyPt[2] << 16) | (keyPt[3] >> 16);
                pt[11] = (keyPt[3] << 16) | (keyPt[4] >> 16);
                pt[12] = (keyPt[4] << 16) | (keyPt[5] >> 16);
                pt[13] = (keyPt[5] << 16) | (keyPt[6] >> 16);
                pt[14] = (keyPt[6] << 16) | (keyPt[7] >> 16);
                pt[15] = (keyPt[7] << 16) | (keyPt[8] >> 16);
                pt[16] = (keyPt[8] << 16) | 0b00000000_00000000_10000000_00000000U;
                // from 9 to 14 =0
                pt[23] = 272; // 34 *8 = 272

                Sha256Fo.Init(pt);
                Sha256Fo.CompressDouble34(pt);

                return pt[0] == keyPt[9];
            }
        }

        public async Task<bool> FindUnknownLocation1(string key, bool comp)
        {
            // [51! / 1! *((51-1)!)] * 58^1
            BigInteger total = 51 * 58;
            report.AddMessageSafe($"Start searching.{Environment.NewLine}Total number of keys to check: {total:n0}");

            Stopwatch watch = Stopwatch.StartNew();
            bool success = await Task.Run(() => SpecialLoopComp1(key, comp));

            watch.Stop();
            report.AddMessageSafe($"Elapsed time: {watch.Elapsed}");
            report.SetKeyPerSecSafe(total, watch.Elapsed.TotalSeconds);

            return success;
        }

        public async Task<bool> FindUnknownLocation2(string key, bool comp)
        {
            // [51! / 2! *((51-2)!)] * 58^2
            BigInteger total = ((51 * 50) / (2 * 1)) * BigInteger.Pow(58, 2);
            report.AddMessageSafe($"Start searching.{Environment.NewLine}Total number of keys to check: {total:n0}");

            Stopwatch watch = Stopwatch.StartNew();
            bool success = await Task.Run(() => SpecialLoopComp2(key, comp));

            watch.Stop();
            report.AddMessageSafe($"Elapsed time: {watch.Elapsed}");
            report.SetKeyPerSecSafe(total, watch.Elapsed.TotalSeconds);

            return success;
        }

        public async Task<bool> FindUnknownLocation3(string key)
        {
            // [51! / 3! *((51-3)!)] * 58^3
            BigInteger total = ((51 * 50 * 49) / (3 * 2 * 1)) * BigInteger.Pow(58, 3);
            report.AddMessageSafe($"Start searching.{Environment.NewLine}Total number of keys to check: {total:n0}");

            Stopwatch watch = Stopwatch.StartNew();
            bool success = await Task.Run(() =>
            {
                return SpecialLoopComp3(key);
            }
            );

            watch.Stop();
            report.AddMessageSafe($"Elapsed time: {watch.Elapsed}");
            report.SetKeyPerSecSafe(total, watch.Elapsed.TotalSeconds);

            return success;
        }


        private async Task FindPrivateKey(string key, char missingChar)
        {
            if (key.Contains(missingChar)) // Length must be correct then
            {
                missCount = key.Count(c => c == missingChar);
                if (inputService.CanBePrivateKey(key, out string error))
                {
                    missingIndexes = new int[missCount];
                    bool isComp = key.Length == ConstantsFO.PrivKeyCompWifLen;
                    report.AddMessageSafe($"{(isComp ? "Compressed" : "Uncompressed")} private key missing {missCount} " +
                                          $"characters was detected.");
                    report.AddMessageSafe($"Total number of keys to check: {GetTotalCount(missCount):n0}");

                    Stopwatch watch = Stopwatch.StartNew();

                    await Task.Run(() =>
                    {
                        if (isComp)
                        {
                            InitializeCompressWif(key.AsSpan(), missingChar);
                            report.AddMessageSafe("Running compressed loop. Please wait.");
                            LoopComp();
                        }
                        else
                        {
                            InitializeUncompressWif(key.AsSpan(), missingChar);
                            report.AddMessageSafe("Running uncompressed loop. Please wait.");
                            LoopUncomp();
                        }
                    }
                    );

                    watch.Stop();
                    report.AddMessageSafe($"Elapsed time: {watch.Elapsed}");
                    report.SetKeyPerSecSafe(GetTotalCount(missCount), watch.Elapsed.TotalSeconds);
                }
                else
                {
                    report.AddMessageSafe(error);
                }
            }
            else // Doesn't have any missing chars so length must be <= max key len
            {
                if (key[0] == ConstantsFO.PrivKeyCompChar1 || key[0] == ConstantsFO.PrivKeyCompChar2)
                {
                    if (key.Length == ConstantsFO.PrivKeyCompWifLen)
                    {
                        report.AddMessageSafe("No character is missing, checking validity of the key itself.");
                        report.AddMessageSafe(inputService.CheckPrivateKey(key));
                    }
                    else if (key.Length == ConstantsFO.PrivKeyCompWifLen - 1)
                    {
                        await FindUnknownLocation1(key, true);
                    }
                    else if (key.Length == ConstantsFO.PrivKeyCompWifLen - 2)
                    {
                        await FindUnknownLocation2(key, true);
                    }
                    else if (key.Length == ConstantsFO.PrivKeyCompWifLen - 3)
                    {
                        await FindUnknownLocation3(key);
                    }
                    else
                    {
                        report.AddMessageSafe("Only 3 missing characters at unkown locations is supported for now.");
                    }
                }
                else if (key[0] == ConstantsFO.PrivKeyUncompChar)
                {
                    if (key.Length == ConstantsFO.PrivKeyUncompWifLen)
                    {
                        report.AddMessageSafe("No character is missing, checking validity of the key itself.");
                        report.AddMessageSafe(inputService.CheckPrivateKey(key));
                    }
                    else if (key.Length == ConstantsFO.PrivKeyUncompWifLen - 1)
                    {
                        await FindUnknownLocation1(key, false);
                    }
                    else if (key.Length == ConstantsFO.PrivKeyUncompWifLen - 2)
                    {
                        await FindUnknownLocation2(key, false);
                    }
                    else
                    {
                        report.AddMessageSafe("Recovering uncompressed private keys with missing characters at unknown locations " +
                                              "is not supported yet.");
                    }
                }
                else
                {
                    report.AddMessageSafe("The given key has an invalid first character.");
                }
            }
        }

        private async Task FindAddress(string address, char missingChar)
        {
            missCount = address.Count(c => c == missingChar);
            if (missCount == 0)
            {
                report.AddMessageSafe("The given input has no missing characters, verifying it as a complete address.");
                report.AddMessageSafe(inputService.CheckBase58Address(address));
            }
            else if (!address.StartsWith(ConstantsFO.B58AddressChar1) && !address.StartsWith(ConstantsFO.B58AddressChar2))
            {
                report.AddMessageSafe($"Base-58 address should start with {ConstantsFO.B58AddressChar1} or " +
                                      $"{ConstantsFO.B58AddressChar2}.");
            }
            else if (address.Length < ConstantsFO.B58AddressMinLen || address.Length > ConstantsFO.B58AddressMaxLen)
            {
                report.AddMessageSafe($"Address length must be between {ConstantsFO.B58AddressMinLen} and " +
                                      $"{ConstantsFO.B58AddressMaxLen} (but it is {address.Length}).");
            }
            else
            {
                missingIndexes = new int[missCount];
                Initialize(address.ToCharArray(), missingChar, InputType.Address);

                report.AddMessageSafe($"Base-58 address missing {missCount} characters was detected.");
                report.AddMessageSafe($"Total number of addresses to check: {GetTotalCount(missCount):n0}");
                report.AddMessageSafe("Going throgh each case. Please wait...");

                Stopwatch watch = Stopwatch.StartNew();

                await Task.Run(() => Loop21());

                watch.Stop();
                report.AddMessageSafe($"Elapsed time: {watch.Elapsed}");
                report.SetKeyPerSecSafe(GetTotalCount(missCount), watch.Elapsed.TotalSeconds);
            }
        }

        private async Task FindBip38(string bip38, char missingChar)
        {
            missCount = bip38.Count(c => c == missingChar);
            if (missCount == 0)
            {
                report.AddMessageSafe("The given BIP38 key has no missing characters, verifying it as a complete key.");
                report.AddMessageSafe(inputService.CheckBase58Bip38(bip38));
            }
            else if (!bip38.StartsWith(ConstantsFO.Bip38Start))
            {
                report.AddMessageSafe($"Base-58 encoded BIP-38 should start with {ConstantsFO.Bip38Start}.");
            }
            else if (bip38.Length != ConstantsFO.Bip38Base58Len)
            {
                report.AddMessageSafe($"Base-58 encoded BIP-38 length must be between {ConstantsFO.Bip38Base58Len}.");
            }
            else
            {
                missingIndexes = new int[missCount];
                Initialize(bip38.ToCharArray(), missingChar, InputType.Bip38);
                report.AddMessageSafe($"Total number of encrypted keys to check: {GetTotalCount(missCount):n0}");
                report.AddMessageSafe("Going throgh each case. Please wait...");

                Stopwatch watch = Stopwatch.StartNew();

                await Task.Run(() => Loop58());

                watch.Stop();
                report.AddMessageSafe($"Elapsed time: {watch.Elapsed}");
                report.SetKeyPerSecSafe(GetTotalCount(missCount), watch.Elapsed.TotalSeconds);
            }
        }

        public async void Find(string key, char missingChar, InputType t, string extra, Services.InputType extraType)
        {
            report.Init();

            if (!inputService.IsMissingCharValid(missingChar))
                report.Fail("Invalid missing character.");
            else if (string.IsNullOrWhiteSpace(key) || !key.All(c => ConstantsFO.Base58Chars.Contains(c) || c == missingChar))
                report.Fail("Input contains invalid base-58 character(s).");
            else
            {
                keyToCheck = key;

                switch (t)
                {
                    case InputType.PrivateKey:
                        if (!inputService.TryGetCompareService(extraType, extra, out comparer))
                        {
                            if (!string.IsNullOrEmpty(extra))
                                report.AddMessage($"Could not instantiate ICompareService (invalid {extraType}).");
                            comparer = null;
                        }
                        // comparer can be null for some of the Loop*() methods
                        await FindPrivateKey(key, missingChar);
                        break;
                    case InputType.Address:
                        await FindAddress(key, missingChar);
                        break;
                    case InputType.Bip38:
                        await FindBip38(key, missingChar);
                        break;
                    default:
                        report.Fail("Given input type is not defined.");
                        return;
                }

                report.Finalize();
            }
        }
    }
}
