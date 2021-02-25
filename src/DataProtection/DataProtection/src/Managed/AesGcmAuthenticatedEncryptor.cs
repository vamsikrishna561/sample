// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#if NETCOREAPP
using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.SP800_108;

namespace Microsoft.AspNetCore.DataProtection.Managed
{
    // An encryptor that uses AesGcm to do encryption
    internal unsafe sealed class AesGcmAuthenticatedEncryptor : ISpanAuthenticatedEncryptor, IDisposable
    {
        // Having a key modifier ensures with overwhelming probability that no two encryption operations
        // will ever derive the same (encryption subkey, MAC subkey) pair. This limits an attacker's
        // ability to mount a key-dependent chosen ciphertext attack. See also the class-level comment
        //  on CngGcmAuthenticatedEncryptor for how this is used to overcome GCM's IV limitations.
        private const int KEY_MODIFIER_SIZE_IN_BYTES = 128 / 8;

        private const int NONCE_SIZE_IN_BYTES = 96 / 8; // GCM has a fixed 96-bit IV
        private const int TAG_SIZE_IN_BYTES = 128 / 8; // we're hardcoding a 128-bit authentication tag size

        // See CngGcmAuthenticatedEncryptor.CreateContextHeader for how these were precomputed

        // 128 "00-01-00-00-00-10-00-00-00-0C-00-00-00-10-00-00-00-10-95-7C-50-FF-69-2E-38-8B-9A-D5-C7-68-9E-4B-9E-2B"
        private static readonly byte[] AES_128_GCM_Header = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x10, 0x95, 0x7C, 0x50, 0xFF, 0x69, 0x2E, 0x38, 0x8B, 0x9A, 0xD5, 0xC7, 0x68, 0x9E, 0x4B, 0x9E, 0x2B };

        // 192 "00-01-00-00-00-18-00-00-00-0C-00-00-00-10-00-00-00-10-0D-AA-01-3A-95-0A-DA-2B-79-8F-5F-F2-72-FA-D3-63"
        private static readonly byte[] AES_192_GCM_Header = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x10, 0x0D, 0xAA, 0x01, 0x3A, 0x95, 0x0A, 0xDA, 0x2B, 0x79, 0x8F, 0x5F, 0xF2, 0x72, 0xFA, 0xD3, 0x63 };

        // 256 00-01-00-00-00-20-00-00-00-0C-00-00-00-10-00-00-00-10-E7-DC-CE-66-DF-85-5A-32-3A-6B-B7-BD-7A-59-BE-45
        private static readonly byte[] AES_256_GCM_Header = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x10, 0xE7, 0xDC, 0xCE, 0x66, 0xDF, 0x85, 0x5A, 0x32, 0x3A, 0x6B, 0xB7, 0xBD, 0x7A, 0x59, 0xBE, 0x45 };

        private static readonly Func<Memory<byte>, HashAlgorithm> _kdkPrfFactory = key => new HMACSHA512(key.ToArray()); // currently hardcoded to SHA512

        private readonly byte[] _contextHeader;

        private readonly Secret _keyDerivationKey;
        private readonly int _derivedkeySizeInBytes;
        private readonly IManagedGenRandom _genRandom;

        public AesGcmAuthenticatedEncryptor(ISecret keyDerivationKey, int derivedKeySizeInBytes, IManagedGenRandom? genRandom = null)
        {
            _keyDerivationKey = new Secret(keyDerivationKey);
            _derivedkeySizeInBytes = derivedKeySizeInBytes;

            switch (_derivedkeySizeInBytes)
            {
                case 16:
                    _contextHeader = AES_128_GCM_Header;
                    break;
                case 24:
                    _contextHeader = AES_192_GCM_Header;
                    break;
                case 32:
                    _contextHeader = AES_256_GCM_Header;
                    break;
                default:
                    throw CryptoUtil.Fail("Unexpected AES key size in bytes only support 16, 24, 32."); // should never happen
            }

            _genRandom = genRandom ?? ManagedGenRandomImpl.Instance;
        }

        public byte[] Decrypt(ArraySegment<byte> ciphertext, ArraySegment<byte> additionalAuthenticatedData)
        {
            ciphertext.Validate();
            additionalAuthenticatedData.Validate();

            return Decrypt(ciphertext.AsSpan(), additionalAuthenticatedData.AsSpan()).ToArray();
        }

        public byte[] Encrypt(ArraySegment<byte> plaintext, ArraySegment<byte> additionalAuthenticatedData)
        {
            var retVal = new byte[checked(KEY_MODIFIER_SIZE_IN_BYTES + NONCE_SIZE_IN_BYTES + plaintext.Count + TAG_SIZE_IN_BYTES)];
            Encrypt(retVal, plaintext, additionalAuthenticatedData);
            return retVal;
        }

        public void Dispose()
        {
            _keyDerivationKey.Dispose();
        }

        public void Encrypt(Span<byte> output, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> additionalAuthenticatedData)
        {
            try
            {
                // In GCM, the encrypted output will be the same length as the plaintext input.
                int keyModifierOffset; // position in ciphertext.Array where key modifier begins
                int nonceOffset; // position in ciphertext.Array where key modifier ends / nonce begins
                int encryptedDataOffset; // position in ciphertext.Array where nonce ends / encryptedData begins
                int tagOffset; // position in ciphertext.Array where encrypted data ends

                checked
                {
                    keyModifierOffset = 0;
                    nonceOffset = keyModifierOffset + KEY_MODIFIER_SIZE_IN_BYTES;
                    encryptedDataOffset = nonceOffset + NONCE_SIZE_IN_BYTES;
                    tagOffset = encryptedDataOffset + plaintext.Length;
                }

                // Randomly generate the key modifier and nonce
                var keyModifier = _genRandom.GenRandom(KEY_MODIFIER_SIZE_IN_BYTES);
                var nonceBytes = _genRandom.GenRandom(NONCE_SIZE_IN_BYTES);

                keyModifier.CopyTo(output);
                nonceBytes.CopyTo(output.Slice(keyModifier.Length));

                // At this point, output := { keyModifier | nonce | _____ | _____ }

                // Use the KDF to generate a new symmetric block cipher key
                // We'll need a temporary buffer to hold the symmetric encryption subkey
                var decryptedKdk = new byte[_keyDerivationKey.Length];
                var derivedKey = new byte[_derivedkeySizeInBytes];
                fixed (byte* __unused__1 = decryptedKdk)
                fixed (byte* __unused__2 = derivedKey)
                {
                    try
                    {
                        _keyDerivationKey.WriteSecretIntoBuffer(decryptedKdk);
                        ManagedSP800_108_CTR_HMACSHA512.DeriveKeysWithContextHeader(
                            kdk: decryptedKdk,
                            label: additionalAuthenticatedData,
                            contextHeader: _contextHeader,
                            context: keyModifier,
                            prfFactory: _kdkPrfFactory,
                            output: derivedKey);

                        // do gcm
                        var nonce = output.Slice(nonceOffset, NONCE_SIZE_IN_BYTES);
                        var tag = output.Slice(tagOffset, TAG_SIZE_IN_BYTES);
                        var encrypted = output.Slice(encryptedDataOffset, plaintext.Length);
                        using var aes = new AesGcm(derivedKey);
                        aes.Encrypt(nonce, plaintext, encrypted, tag);

                        // At this point, output := { preBuffer | keyModifier | nonce | encryptedData | authenticationTag | postBuffer }
                        // And we're done!
                    }
                    finally
                    {
                        // delete since these contain secret material
                        Array.Clear(decryptedKdk, 0, decryptedKdk.Length);
                        Array.Clear(derivedKey, 0, derivedKey.Length);
                    }
                }
            }
            catch (Exception ex) when (ex.RequiresHomogenization())
            {
                // Homogenize all exceptions to CryptographicException.
                throw Error.CryptCommon_GenericError(ex);
            }
        }

        public Span<byte> Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> additionalAuthenticatedData)
        {
            // Argument checking: input must at the absolute minimum contain a key modifier, nonce, and tag
            if (ciphertext.Length < KEY_MODIFIER_SIZE_IN_BYTES + NONCE_SIZE_IN_BYTES + TAG_SIZE_IN_BYTES)
            {
                throw Error.CryptCommon_PayloadInvalid();
            }

            // Assumption: pbCipherText := { keyModifier || nonce || encryptedData || authenticationTag }
            var plaintextBytes = ciphertext.Length - (KEY_MODIFIER_SIZE_IN_BYTES + NONCE_SIZE_IN_BYTES + TAG_SIZE_IN_BYTES);
            var plaintext = new byte[plaintextBytes];

            try
            {
                // Step 1: Extract the key modifier from the payload.

                int keyModifierOffset; // position in ciphertext.Array where key modifier begins
                int nonceOffset; // position in ciphertext.Array where key modifier ends / nonce begins
                int encryptedDataOffset; // position in ciphertext.Array where nonce ends / encryptedData begins
                int tagOffset; // position in ciphertext.Array where encrypted data ends

                checked
                {
                    keyModifierOffset = 0;
                    nonceOffset = keyModifierOffset + KEY_MODIFIER_SIZE_IN_BYTES;
                    encryptedDataOffset = nonceOffset + NONCE_SIZE_IN_BYTES;
                    tagOffset = encryptedDataOffset + plaintextBytes;
                }

                var keyModifier = ciphertext.Slice(keyModifierOffset, KEY_MODIFIER_SIZE_IN_BYTES);

                // Step 2: Decrypt the KDK and use it to restore the original encryption and MAC keys.
                // We pin all unencrypted keys to limit their exposure via GC relocation.

                var decryptedKdk = new byte[_keyDerivationKey.Length];
                var derivedKey = new byte[_derivedkeySizeInBytes];

                fixed (byte* __unused__1 = decryptedKdk)
                fixed (byte* __unused__2 = derivedKey)
                {
                    try
                    {
                        _keyDerivationKey.WriteSecretIntoBuffer(new ArraySegment<byte>(decryptedKdk));
                        ManagedSP800_108_CTR_HMACSHA512.DeriveKeysWithContextHeader(
                            kdk: decryptedKdk,
                            label: additionalAuthenticatedData,
                            contextHeader: _contextHeader,
                            context: keyModifier,
                            prfFactory: _kdkPrfFactory,
                            output: derivedKey.AsSpan());

                        // Perform the decryption operation
                        var nonce = ciphertext.Slice(nonceOffset, NONCE_SIZE_IN_BYTES);
                        var tag = ciphertext.Slice(tagOffset, TAG_SIZE_IN_BYTES);
                        var encrypted = ciphertext.Slice(encryptedDataOffset, plaintextBytes);
                        using var aes = new AesGcm(derivedKey);
                        aes.Decrypt(nonce, encrypted, tag, plaintext);
                        return plaintext;
                    }
                    finally
                    {
                        // delete since these contain secret material
                        Array.Clear(decryptedKdk, 0, decryptedKdk.Length);
                        Array.Clear(derivedKey, 0, derivedKey.Length);
                    }
                }
            }
            catch (Exception ex) when (ex.RequiresHomogenization())
            {
                // Homogenize all exceptions to CryptographicException.
                throw Error.CryptCommon_GenericError(ex);
            }
        }
    }
}
#endif
