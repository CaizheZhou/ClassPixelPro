$hasSecrets = ($null -ne $env:API_SIGNING_KEY -and $null -ne $env:API_SIGNING_KEY_PS) ? "true" : "false"

Write-Output "Secrets filled: $hasSecrets"

$code = "
namespace ClassPixelPro.Services.SpeechService{
    public static partial class GptSovitsSecrets
    {
        public const string PrivateKey = 
`"`"`"
${env:API_SIGNING_KEY}
`"`"`";
    
        public const string PrivateKeyPassPhrase = 
`"`"`"
${env:API_SIGNING_KEY_PS}
`"`"`";
    
        public const bool IsSecretsFilled = ${hasSecrets};
    }
}
"

Set-Content -Path ./ClassPixelPro/secrets.g.cs -Value $code