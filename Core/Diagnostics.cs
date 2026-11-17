namespace AiVoiceBridge.Core;

public sealed class LevelIndicator
{
    private long _lastOverload=long.MinValue;
    public double Level { get; private set; }
    public bool Available { get; private set; }
    public bool Overload { get; private set; }
    public string Label=>!Available?"Нет сигнала уровня":Overload?$"{Level:0}% · перегруз / близко к пределу":$"{Level:0}%";
    public string Color=>Overload?"#EC727F":"#8BD5A8";
    public void Update(double? percent,long now)
    {
        Available=percent.HasValue&&double.IsFinite(percent.Value);
        Level=Available?Math.Clamp(percent!.Value,0,100):0;
        if(!Available) { _lastOverload=long.MinValue; Overload=false; return; }
        if(percent>=98)_lastOverload=now;
        Overload=_lastOverload!=long.MinValue&&now-_lastOverload<=1500;
    }
}
public static class ConnectionExplanation
{
    public static string Title(string? error)
    {
        if(string.IsNullOrWhiteSpace(error))return "Проверьте подключение";
        if(error.Contains("не запущено"))return error.Contains("Dota")?"Запустите Dota 2":error.Contains("CS2")?"Запустите CS2":"Запустите Discord";
        if(error.Contains("закрыт"))return "Приложение закрыто";
        if(error.Contains("несколько экземпляров"))return "Оставьте один экземпляр приложения";
        if(error.Contains("устройств",StringComparison.OrdinalIgnoreCase)||error.Contains("маршрут",StringComparison.OrdinalIgnoreCase)||error.Contains("петля"))return "Проверьте устройства и маршруты";
        if(error.Contains("доступ",StringComparison.OrdinalIgnoreCase))return "Нет доступа к аудио";
        return "Не удалось подключить звук";
    }
}
