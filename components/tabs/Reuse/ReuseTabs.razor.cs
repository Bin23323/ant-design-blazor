﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace AntDesign
{
    public partial class ReuseTabs : AntDomComponentBase
    {
        [Parameter]
        public string TabPaneClass { get; set; }

        [Parameter]
        public bool Draggable { get; set; }

        [Parameter]
        public TabSize Size { get; set; }

        [Parameter]
        public RenderFragment<ReuseTabsPageItem> Body { get; set; } = context => context.Body;

        [Parameter]
        public ReuseTabsLocale Locale { get; set; } = LocaleProvider.CurrentLocale.ReuseTabs;

        [CascadingParameter]
        private RouteData RouteData { get; set; }

        [Inject]
        private NavigationManager Navmgr { get; set; }

        [Inject]
        private ReuseTabsService ReuseTabsService { get; set; }

        private readonly Dictionary<string, ReuseTabsPageItem> _pageMap = new();

        private string CurrentUrl
        {
            get => "/" + Navmgr.ToBaseRelativePath(Navmgr.Uri);
            set => Navmgr.NavigateTo(value.StartsWith("/") ? value[1..] : value);
        }

        private ReuseTabsPageItem[] Pages => _pageMap.Values.Where(x => !x.Ignore).OrderBy(x => x.CreatedAt).ThenBy(x => x.Order).ToArray();

        protected override void OnInitialized()
        {
            this.ScanReuseTabsPageAttribute();
            ReuseTabsService.OnClosePage += RemovePage;
            ReuseTabsService.OnCloseOther += RemoveOther;
            ReuseTabsService.OnCloseAll += RemoveAll;
            ReuseTabsService.OnCloseCurrent += RemoveCurrent;
            ReuseTabsService.OnUpdate += UpdateState;
            ReuseTabsService.OnReloadPage += ReloadPage;
        }

        protected override void Dispose(bool disposing)
        {
            ReuseTabsService.OnClosePage -= RemovePage;
            ReuseTabsService.OnCloseOther -= RemoveOther;
            ReuseTabsService.OnCloseAll -= RemoveAll;
            ReuseTabsService.OnCloseCurrent -= RemoveCurrent;
            ReuseTabsService.OnUpdate -= UpdateState;
            ReuseTabsService.OnReloadPage -= ReloadPage;

            base.Dispose(disposing);
        }

        public override Task SetParametersAsync(ParameterView parameters)
        {
            if (parameters.TryGetValue(nameof(RouteData), out RouteData routeData))
            {
                var reuseTabsPageItem = _pageMap.ContainsKey(CurrentUrl) ? _pageMap[CurrentUrl] : null;
                if (reuseTabsPageItem == null)
                {
                    reuseTabsPageItem = new ReuseTabsPageItem
                    {
                        Url = CurrentUrl,
                        CreatedAt = DateTime.Now,
                    };

                    _pageMap[CurrentUrl] = reuseTabsPageItem;
                }

                reuseTabsPageItem.Body ??= builder => RenderPageWithParameters(builder, routeData, reuseTabsPageItem);
            }

            return base.SetParametersAsync(parameters);
        }

        private static void RenderPageWithParameters(RenderTreeBuilder builder, RouteData routeData, ReuseTabsPageItem item)
        {
#if NET8_0_OR_GREATER
            builder.OpenComponent<CascadingModelBinder>(0);
            builder.AddComponentParameter(1, nameof(CascadingModelBinder.ChildContent), (RenderFragment<ModelBindingContext>)RenderPageWithContext);
            builder.CloseComponent();

            RenderFragment RenderPageWithContext(ModelBindingContext context) => RenderPageCore;
#else
            RenderPageCore(builder);
#endif
            void RenderPageCore(RenderTreeBuilder builder)
            {
                builder.OpenComponent(0, routeData.PageType);

                foreach (var kvp in routeData.RouteValues)
                {
#if NET8_0_OR_GREATER
                    builder.AddComponentParameter(1, kvp.Key, kvp.Value);
#else
                    builder.AddAttribute(1, kvp.Key, kvp.Value);
#endif
                }

                builder.AddComponentReferenceCapture(2, @ref =>
                {
                    GetPageInfo(item, routeData.PageType, item.Url, @ref);
                });

                builder.CloseComponent();
            }
        }

        private static void GetPageInfo(ReuseTabsPageItem pageItem, Type pageType, string url, object page)
        {
            if (page is IReuseTabsPage resuse)
            {
                pageItem.Title = resuse.GetPageTitle();
            }

            var attributes = pageType.GetCustomAttributes(true);

            if (attributes.FirstOrDefault(x => x is ReuseTabsPageTitleAttribute) is ReuseTabsPageTitleAttribute titleAttr && titleAttr != null)
            {
                pageItem.Title ??= titleAttr.Title?.ToRenderFragment();
            }

            if (attributes.FirstOrDefault(x => x is ReuseTabsPageAttribute) is ReuseTabsPageAttribute attr && attr != null)
            {
                pageItem.Title ??= attr.Title?.ToRenderFragment();
                pageItem.Ignore = attr.Ignore;
                pageItem.Closable = attr.Closable;
                pageItem.Pin = attr.Pin;
                pageItem.KeepAlive = attr.KeepAlive;
                pageItem.Order = attr.Order;
            }

            pageItem.Title ??= url.ToRenderFragment();
        }

        /// <summary>
        /// 获取所有程序集
        /// </summary>
        /// <returns></returns>
        private IEnumerable<Assembly> GetAllAssembly()
        {
            IEnumerable<Assembly> assemblies = new List<Assembly>();
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly == null) return assemblies;
            var referencedAssemblies = entryAssembly.GetReferencedAssemblies().Select(Assembly.Load);
            assemblies = new List<Assembly> { entryAssembly }.Union(referencedAssemblies);
            return assemblies;
        }

        /// <summary>
        /// 扫描 ReuseTabsPageAttribute 特性
        /// </summary>
        private void ScanReuseTabsPageAttribute()
        {
            var list = GetAllAssembly();

            foreach (var item in list)
            {
                var allClass = item.ExportedTypes
                    .Where(w => w.GetCustomAttribute<ReuseTabsPageAttribute>()?.Pin == true);
                foreach (var pageType in allClass)
                {
                    this.AddReuseTabsPageItem(pageType);
                }
            }
        }

        private void AddReuseTabsPageItem(Type pageType)
        {
            var routeAttribute = pageType.GetCustomAttribute<RouteAttribute>();
            var reuseTabsAttribute = pageType.GetCustomAttribute<ReuseTabsPageAttribute>();

            var url = reuseTabsAttribute?.PinUrl ?? routeAttribute.Template;
            var reuseTabsPageItem = new ReuseTabsPageItem();
            GetPageInfo(reuseTabsPageItem, pageType, url, Activator.CreateInstance(pageType));
            reuseTabsPageItem.CreatedAt = DateTime.MinValue;
            reuseTabsPageItem.Url = url;
            _pageMap[url] = reuseTabsPageItem;
        }

        private void RemovePage(string key)
        {
            var reuseTabsPageItem = Pages.FirstOrDefault(w => w.Url == key);
            if (reuseTabsPageItem?.Pin == true)
            {
                return;
            }

            RemovePageBase(key);
            StateHasChanged();
        }

        private void RemoveOther(string key)
        {
            foreach (var item in Pages.Where(x => x.Closable && x.Url != key && !x.Pin))
            {
                RemovePageBase(item.Url);
            }
            StateHasChanged();
        }

        private void RemoveAll()
        {
            foreach (var item in Pages.Where(x => x.Closable && !x.Pin))
            {
                RemovePageBase(item.Url);
            }
            StateHasChanged();
        }

        private void RemoveCurrent()
        {
            RemovePage(this.CurrentUrl);
        }

        private void UpdateState()
        {
            StateHasChanged();
        }

        private void ReloadPage(string key)
        {
            key ??= CurrentUrl;
            _pageMap[key].Body = null;
            if (CurrentUrl == key)
            {
                CurrentUrl = key; // auto reload current page, and other page would be load by tab navigation. 
            }
            StateHasChanged();
        }

        private void RemovePageBase(string key)
        {
            _pageMap.Remove(key);
        }

        private void RemovePageWithRegex(string pattern)
        {
            foreach (var key in _pageMap.Keys)
            {
                if (Regex.IsMatch(key, pattern))
                {
                    _pageMap.Remove(key);
                }
            }
        }

    }
}
