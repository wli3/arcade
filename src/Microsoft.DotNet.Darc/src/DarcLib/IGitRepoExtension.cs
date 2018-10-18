// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Microsoft.DotNet.DarcLib
{
    public static class IGitRepoExtension
    {
        public static string GetDecodedContent(this IGitRepo gitRepo, string encodedContent)
        {
            var serializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

            byte[] content = Convert.FromBase64String(encodedContent);
            return Encoding.UTF8.GetString(content);
        }
    }
}
