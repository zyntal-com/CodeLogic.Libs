namespace CL.SystemStats.Models;

/// <summary>
/// One temperature sensor reading.
/// </summary>
/// <param name="Sensor">Sensor name, e.g. <c>thermal_zone0:x86_pkg_temp</c> or <c>coretemp:Package id 0</c>.</param>
/// <param name="Celsius">Temperature in degrees Celsius.</param>
public record TemperatureReading(string Sensor, double Celsius);
