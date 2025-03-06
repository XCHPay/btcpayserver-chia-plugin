using System;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.Chia.Services;

namespace BTCPayServer.Plugins.Chia.Configuration;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public class ChiaMasterPublicKeyAttribute : ValidationAttribute
{
    public ChiaMasterPublicKeyAttribute()
    {
        this.ErrorMessage = "{0} is not a valid master public key.";
    }

    public override bool IsValid(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is not string valueAsString)
        {
            return false;
        }

        return ChiaKeyHelper.IsValidChiaKey(valueAsString);
    }
}