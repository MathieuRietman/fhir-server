﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.Health.Fhir.Api.Features.Routing
{
    public class CompartmentTypesRouteConstraint : IRouteConstraint
    {
        public bool Match(HttpContext httpContext, IRouter route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
        {
            EnsureArg.IsNotNull(httpContext, nameof(httpContext));
            EnsureArg.IsNotNull(route, nameof(route));
            EnsureArg.IsNotNullOrEmpty(routeKey, nameof(routeKey));
            EnsureArg.IsNotNull(values, nameof(values));

            if (values.TryGetValue(KnownActionParameterNames.CompartmentType, out var compartmentTypeObj) && compartmentTypeObj is string compartmentType && !string.IsNullOrEmpty(compartmentType))
            {
                return Enum.TryParse<CompartmentType>(compartmentType, true, out var compartmentTypeEnumValue);
            }

            return false;
        }
    }
}
