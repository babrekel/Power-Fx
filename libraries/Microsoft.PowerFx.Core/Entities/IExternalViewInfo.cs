//------------------------------------------------------------------------------
// <copyright company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;

namespace PowerApps.Language.Entities
{
    public interface IExternalViewInfo : IDisplayMapped<Guid>
    {
        string Name { get; }
        string RelatedEntityName { get; }
    }
}