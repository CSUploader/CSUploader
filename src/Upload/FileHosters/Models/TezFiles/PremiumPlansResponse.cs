// <copyright file="PremiumPlansResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.Serialization;

namespace CSUploader.Upload.FileHosters.Models.TezFiles
{
    // GET /v1/users/me/plans
    [Serializable]
    public class PremiumPlansResponse : Dictionary<string, PremiumPlan>
    {
        public PremiumPlansResponse()
            : base()
        {
        }

        public PremiumPlansResponse(int capacity)
            : base(capacity)
        {
        }

        public PremiumPlansResponse(IEqualityComparer<string> comparer)
            : base(comparer)
        {
        }

        public PremiumPlansResponse(IDictionary<string, PremiumPlan> dictionary)
            : base(dictionary)
        {
        }

        public PremiumPlansResponse(int capacity, IEqualityComparer<string> comparer)
            : base(capacity, comparer)
        {
        }

        public PremiumPlansResponse(IDictionary<string, PremiumPlan> dictionary, IEqualityComparer<string> comparer)
            : base(dictionary, comparer)
        {
        }

        protected PremiumPlansResponse(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
