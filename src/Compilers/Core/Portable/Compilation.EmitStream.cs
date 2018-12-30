﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    public abstract partial class Compilation
    {
        /// <summary>
        /// Describes the kind of real signing that is being done during Emit. In the case of public signing
        /// this value will be <see cref="None"/>.
        /// </summary>
        internal enum EmitStreamSignKind
        {
            None,
            SignedWithBulider,
            SignedWithFile,
        }

        /// <summary>
        /// This type abstracts away the legacy COM based signing implementation for PE streams. Under the hood
        /// a temporary file must be created on disk (at the last possible moment), emitted to, signed on disk
        /// and then copied back to the original <see cref="Stream"/>. Only when legacy signing is enabled though.
        /// </summary>
        internal sealed class EmitStream
        {
            private readonly EmitStreamProvider _emitStreamProvider;
            private readonly EmitStreamSignKind _emitStreamSignKind;
            private readonly StrongNameProvider _strongNameProvider;
            private (Stream tempStream, string tempFilePath)? _tempInfo;

            /// <summary>
            /// The <see cref="Stream"/> that is being emitted into. This value should _never_ be 
            /// Dispose. It is either returned from the <see cref="EmitStreamProvider"/> instance in
            /// which case it is owned by that. Or it is just an alias for the value that is stored 
            /// in <see cref="_tempInfo"/> in which case it will be disposed from there.
            /// </summary>
            private Stream _stream;

            internal EmitStream(
                EmitStreamProvider emitStreamProvider,
                EmitStreamSignKind emitStreamSignKind,
                StrongNameProvider strongNameProvider)
            {
                Debug.Assert(emitStreamProvider != null);
                Debug.Assert(strongNameProvider != null || emitStreamSignKind == EmitStreamSignKind.None);
                _emitStreamProvider = emitStreamProvider;
                _emitStreamSignKind = emitStreamSignKind;
                _strongNameProvider = strongNameProvider;
            }

            internal Func<Stream> GetCreateStreamFunc(DiagnosticBag diagnostics)
            {
                return () => CreateStream(diagnostics);
            }

            internal void Dispose()
            {
                _tempInfo?.tempStream.Dispose();
                _tempInfo = null;

                // The _stream value is deliberately excluded from being disposed here. That value is not 
                // owned by this type.
                _stream = null;
            }

            /// <summary>
            /// Create the stream which should be used for Emit. This should only be called one time.
            /// </summary>
            private Stream CreateStream(DiagnosticBag diagnostics)
            {
                Debug.Assert(_stream == null);
                Debug.Assert(diagnostics != null);

                if (diagnostics.HasAnyErrors())
                {
                    return null;
                }

                _stream = _emitStreamProvider.GetOrCreateStream(diagnostics);
                if (_stream == null)
                {
                    Debug.Assert(diagnostics.HasAnyErrors());
                    return null;
                }

                // If the current strong name provider is the Desktop version, signing can only be done to on-disk files.
                // If this binary is configured to be signed, create a temp file, output to that
                // then stream that to the stream that this method was called with. Otherwise output to the
                // stream that this method was called with.
                if (_emitStreamSignKind == EmitStreamSignKind.SignedWithFile)
                {
                    Debug.Assert(_strongNameProvider != null);

                    try
                    {
                        Func<string, Stream> streamConstructor = path => new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);

                        var tempDir = _strongNameProvider.FileSystem.GetTempPath();
                        var tempFilePath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
                        var tempStream = FileUtilities.CreateFileStreamChecked(streamConstructor, tempFilePath);

                        _tempInfo = (tempStream, tempFilePath);
                        return tempStream;
                    }
                    catch (IOException e)
                    {
                        throw new Cci.PeWritingException(e);
                    }
                }
                else
                {
                    return _stream;
                }
            }

            internal bool Complete(StrongNameKeys strongNameKeys, CommonMessageProvider messageProvider, DiagnosticBag diagnostics)
            {
                Debug.Assert(_stream != null);

                if (_tempInfo.HasValue)
                {
                    Debug.Assert(_emitStreamSignKind == EmitStreamSignKind.SignedWithFile);
                    var (tempStream, tempFilePath) = _tempInfo.Value;

                    try
                    {
                        // Dispose the temp stream to ensure all of the contents are written to 
                        // disk.
                        tempStream.Dispose();

                        _strongNameProvider.SignFile(strongNameKeys, tempFilePath);

                        using (var tempFileStream = new FileStream(tempFilePath, FileMode.Open))
                        {
                            tempFileStream.CopyTo(_stream);
                        }
                    }
                    catch (DesktopStrongNameProvider.ClrStrongNameMissingException)
                    {
                        diagnostics.Add(StrongNameKeys.GetError(strongNameKeys.KeyFilePath, strongNameKeys.KeyContainer,
                            new CodeAnalysisResourcesLocalizableErrorArgument(nameof(CodeAnalysisResources.AssemblySigningNotSupported)), messageProvider));
                        return false;
                    }
                    catch (IOException ex)
                    {
                        diagnostics.Add(StrongNameKeys.GetError(strongNameKeys.KeyFilePath, strongNameKeys.KeyContainer, ex.Message, messageProvider));
                        return false;
                    }
                    finally
                    {
                        try
                        {
                            File.Delete(tempFilePath);
                        }
                        catch
                        {
                            // Not much to do if we can't delete from the temp directory
                        }

                        tempStream.Dispose();
                        _tempInfo = null;
                        _stream = null;
                    }
                }

                _stream = null;
                return true;
            }
        }
    }
}
