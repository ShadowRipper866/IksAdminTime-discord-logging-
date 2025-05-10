using CounterStrikeSharp.API.Core;

namespace IksAdminTime
{
	public class IksAdminTimeConfig : BasePluginConfig
	{
		public int ServerID { get; set; } = 0;

		public string ServerName { get; set; } = "";
		public string DiscordWebHookUrl { get; set; } = "";
	}
}