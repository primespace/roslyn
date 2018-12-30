﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;

namespace Microsoft.CodeAnalysis.Test.Utilities
{
    internal sealed class TestStrongNameFileSystem : StrongNameFileSystem
    {
        internal Func<string, byte[]> ReadAllBytesFunc { get; set; }

        internal TestStrongNameFileSystem()
        {
            ReadAllBytesFunc = base.ReadAllBytes;
        }

        internal override byte[] ReadAllBytes(string fullPath) => ReadAllBytesFunc(fullPath);
    }
}
