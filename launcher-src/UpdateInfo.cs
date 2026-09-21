namespace ForgeNeoLauncher
{
    /// <summary>一次「检测更新」的结果状态。</summary>
    public enum UpdateState
    {
        /// <summary>还没检测过</summary>
        Unknown,
        /// <summary>已是最新</summary>
        UpToDate,
        /// <summary>有新版本</summary>
        UpdateAvailable,
        /// <summary>检测过程出错（网络 / 不是仓库 / 读不到提交）</summary>
        Error
    }

    /// <summary>
    /// 内核的检测结果。
    ///
    /// <para><b>为什么把内核与插件的模型彻底分开</b>：v0.30 及以前两者共用一个
    /// <c>UpdateItem</c>，靠 <c>IsCore</c> 布尔区分，于是同一个字段要回答两个问题 ——
    /// <c>CurrentVersion</c> 对内核是「<c>forge_version.py</c> 里的 release 号<b>或</b> git 短号」，
    /// 对插件则是「git 短号」；<c>RemoteUrl</c> 对内核是个写死的常量，对插件是
    /// <c>git remote get-url</c> 读出来的。这种「一个字段两种语义」正是本项目
    /// 反复踩过的<b>判据兼职</b>（同一个坑已出现 4 次，最近一次是 v0.30 的端口）。</para>
    ///
    /// <para>拆开之后各自的字段只说自己的话，界面上要显示什么也能各取所需。</para>
    /// </summary>
    public sealed class CoreUpdateInfo
    {
        /// <summary>显示名（固定为「Forge Neo 内核」）</summary>
        public string Name { get; set; } = "Forge Neo 内核";

        /// <summary>本地仓库目录（= 包根）</summary>
        public string Path { get; set; } = "";

        /// <summary>上游地址（常量）</summary>
        public string RemoteUrl { get; set; } = "";

        /// <summary>跟踪的分支（常量 neo）</summary>
        public string Branch { get; set; } = "";

        /// <summary>当前版本：git 仓库给短号，否则给 release 号</summary>
        public string CurrentVersion { get; set; } = "";

        /// <summary>最新版本：拿得到提交号就给短号，否则给 release 号</summary>
        public string LatestVersion { get; set; } = "";

        /// <summary>本地这次提交的日期</summary>
        public string CurrentDate { get; set; } = "";

        /// <summary>上游最新提交的说明（best-effort，取不到就退成时间）</summary>
        public string LatestMessage { get; set; } = "";

        public UpdateState State { get; set; } = UpdateState.Unknown;

        /// <summary>一句话状态说明（界面上直接显示）</summary>
        public string Message { get; set; } = "";

        /// <summary>只有「确实有新版本」时才允许点更新按钮</summary>
        public bool CanUpdate => State == UpdateState.UpdateAvailable;
    }

    /// <summary>
    /// 一个扩展（插件）条目。
    ///
    /// <para>它与 <see cref="CoreUpdateInfo"/> 是<b>两类东西</b>，不是一个模板的两种配置：
    /// 内核只有一个、上游写死、更新走 <c>checkout -f</c>；插件有 N 个、各有各的 origin、
    /// 更新走 <c>reset --hard</c>，而且还能装 / 卸 / 启用禁用。共用一个模型只会让
    /// 每处逻辑都得先问一句「这条是不是内核」。</para>
    /// </summary>
    public sealed class ExtensionInfo
    {
        /// <summary>目录名（也就是 Forge 眼里的扩展 id）</summary>
        public string Name { get; set; } = "";

        /// <summary>扩展目录的完整路径</summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// 是否由 git 管理。
        ///
        /// <para>⚠ <b>false 不等于「坏了」</b>：手工解压 / 拷贝进来的扩展本来就没有
        /// <c>.git</c>（用户从压缩包装的很多扩展都是这样）。v0.30 及以前的实现
        /// 遇到这种目录直接 <c>continue</c>，于是它们<b>在列表里根本不出现</b> ——
        /// 用户会以为「启动器没认出来 = 没装上」。现在照样列出来，只是不提供更新。</para>
        /// </summary>
        public bool IsGit { get; set; }

        /// <summary>origin 地址（非 git 扩展为空）</summary>
        public string RemoteUrl { get; set; } = "";

        /// <summary>当前分支（非 git 扩展为空）</summary>
        public string Branch { get; set; } = "";

        /// <summary>本地提交短号（非 git 扩展为空）</summary>
        public string CurrentVersion { get; set; } = "";

        /// <summary>本地提交日期（非 git 扩展为空）</summary>
        public string CurrentDate { get; set; } = "";

        /// <summary>远程最新提交短号</summary>
        public string LatestVersion { get; set; } = "";

        /// <summary>远程最新提交说明</summary>
        public string LatestMessage { get; set; } = "";

        /// <summary>
        /// 是否启用。<b>由两处共同决定</b>：扩展目录下的 <c>disabled</c> 空文件，
        /// 以及 WebUI <c>config.json</c> 的 <c>disabled_extensions</c> 数组 ——
        /// 任一处在名单里就算禁用（见 <see cref="ExtensionManager.SetEnabled"/>）。
        /// </summary>
        public bool Enabled { get; set; } = true;

        public UpdateState State { get; set; } = UpdateState.Unknown;

        /// <summary>一句话状态说明（界面上直接显示）</summary>
        public string Message { get; set; } = "";

        /// <summary>能不能点「更新」—— 非 git 的扩展没有「最新提交」这个概念</summary>
        public bool CanUpdate => IsGit && State == UpdateState.UpdateAvailable;

        /// <summary>来源的显示文案（非 git 的写「本地目录」而不是空着，免得看着像出错）</summary>
        public string SourceText => IsGit
            ? (string.IsNullOrEmpty(RemoteUrl) ? "git（未配 origin）" : RemoteUrl)
            : "本地目录（非 git，不支持更新）";
    }
}
