using System;
using System.Net.Http;

namespace ForgeNeoLauncher
{
    /// <summary>
    /// 「检测更新」用的 HTTP 客户端 —— 内核侧与插件侧共用一份。
    ///
    /// <para><b>为什么不各建一个</b>：<c>HttpClient</c> 是设计成长期复用的
    /// （每次 new 一个都会新开一组连接池，短时间连着测十几个插件就会大量
    /// 堆积 TIME_WAIT）。两处都只是"打一个 GitHub 只读接口"，
    /// 统一 UA 与超时反而更省事。</para>
    ///
    /// <para>⚠ <b>UA 必须设</b>：GitHub 的 API 对没有 User-Agent 的请求直接回 403，
    /// 而 403 的报错看起来像"限流"，很容易往错误方向排查。</para>
    /// </summary>
    internal static class UpdateHttp
    {
        /// <summary>25 秒超时 —— 国内直连 raw.githubusercontent 常常要十几秒</summary>
        public static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        static UpdateHttp()
        {
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeNeoLauncher/1.0");
            Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
    }
}
