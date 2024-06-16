﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using ICSharpCode.ILSpy.AppEnv;
using ICSharpCode.ILSpy.Docking;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.Extensions;
using ICSharpCode.ILSpyX.Search;

namespace ICSharpCode.ILSpy.Search
{
	/// <summary>
	/// Search pane
	/// </summary>
	public partial class SearchPane : UserControl
	{
		const int MAX_RESULTS = 1000;
		const int MAX_REFRESH_TIME_MS = 10; // More means quicker forward of data, less means better responsibility
		RunningSearch currentSearch;
		bool runSearchOnNextShow;
		IComparer<SearchResult> resultsComparer;
		FilterSettings filterSettings;

		public static readonly DependencyProperty ResultsProperty =
			DependencyProperty.Register("Results", typeof(ObservableCollection<SearchResult>), typeof(SearchPane),
				new PropertyMetadata(new ObservableCollection<SearchResult>()));
		public ObservableCollection<SearchResult> Results {
			get { return (ObservableCollection<SearchResult>)GetValue(ResultsProperty); }
		}

		public SearchPane()
		{
			InitializeComponent();
			searchModeComboBox.Items.Add(new { Image = Images.Library, Name = "Types and Members" });
			searchModeComboBox.Items.Add(new { Image = Images.Class, Name = "Type" });
			searchModeComboBox.Items.Add(new { Image = Images.Property, Name = "Member" });
			searchModeComboBox.Items.Add(new { Image = Images.Method, Name = "Method" });
			searchModeComboBox.Items.Add(new { Image = Images.Field, Name = "Field" });
			searchModeComboBox.Items.Add(new { Image = Images.Property, Name = "Property" });
			searchModeComboBox.Items.Add(new { Image = Images.Event, Name = "Event" });
			searchModeComboBox.Items.Add(new { Image = Images.Literal, Name = "Constant" });
			searchModeComboBox.Items.Add(new { Image = Images.Library, Name = "Metadata Token" });
			searchModeComboBox.Items.Add(new { Image = Images.Resource, Name = "Resource" });
			searchModeComboBox.Items.Add(new { Image = Images.Assembly, Name = "Assembly" });
			searchModeComboBox.Items.Add(new { Image = Images.Namespace, Name = "Namespace" });

			ContextMenuProvider.Add(listBox);
			MainWindow.Instance.CurrentAssemblyListChanged += MainWindow_Instance_CurrentAssemblyListChanged;
			filterSettings = MainWindow.Instance.SessionSettings.FilterSettings;
			CompositionTarget.Rendering += UpdateResults;

			// This starts empty search right away, so do at the end (we're still in ctor)
			searchModeComboBox.SelectedIndex = (int)MainWindow.Instance.SessionSettings.SelectedSearchMode;
		}

		void MainWindow_Instance_CurrentAssemblyListChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			if (IsVisible)
			{
				StartSearch(this.SearchTerm);
			}
			else
			{
				StartSearch(null);
				runSearchOnNextShow = true;
			}
		}

		internal void UpdateFilter(FilterSettings settings)
		{
			this.filterSettings = settings;

			if (IsVisible)
			{
				StartSearch(this.SearchTerm);
			}
			else
			{
				StartSearch(null);
				runSearchOnNextShow = true;
			}
		}

		public void Show()
		{
			if (!IsVisible)
			{
				DockWorkspace.Instance.ToolPanes.Single(p => p.ContentId == SearchPaneModel.PaneContentId).IsVisible = true;
				if (runSearchOnNextShow)
				{
					runSearchOnNextShow = false;
					StartSearch(this.SearchTerm);
				}
			}
			Dispatcher.BeginInvoke(
				DispatcherPriority.Background,
				new Action(
					delegate {
						searchBox.Focus();
						searchBox.SelectAll();
					}));
		}

		public static readonly DependencyProperty SearchTermProperty =
			DependencyProperty.Register("SearchTerm", typeof(string), typeof(SearchPane),
				new FrameworkPropertyMetadata(string.Empty, OnSearchTermChanged));

		public string SearchTerm {
			get { return (string)GetValue(SearchTermProperty); }
			set { SetValue(SearchTermProperty, value); }
		}

		static void OnSearchTermChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
		{
			((SearchPane)o).StartSearch((string)e.NewValue);
		}

		void SearchModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			MainWindow.Instance.SessionSettings.SelectedSearchMode = (SearchMode)searchModeComboBox.SelectedIndex;
			StartSearch(this.SearchTerm);
		}

		void ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			JumpToSelectedItem();
			e.Handled = true;
		}

		void ListBox_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Return)
			{
				e.Handled = true;
				JumpToSelectedItem();
			}
			else if (e.Key == Key.Up && listBox.SelectedIndex == 0)
			{
				e.Handled = true;
				listBox.SelectedIndex = -1;
				searchBox.Focus();
			}
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Key == Key.T && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
			{
				searchModeComboBox.SelectedIndex = (int)SearchMode.Type;
				e.Handled = true;
			}
			else if (e.Key == Key.M && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
			{
				searchModeComboBox.SelectedIndex = (int)SearchMode.Member;
				e.Handled = true;
			}
			else if (e.Key == Key.S && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
			{
				searchModeComboBox.SelectedIndex = (int)SearchMode.Literal;
				e.Handled = true;
			}
		}

		void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Down && listBox.HasItems)
			{
				e.Handled = true;
				listBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
				listBox.SelectedIndex = 0;
			}
		}

		void UpdateResults(object sender, EventArgs e)
		{
			if (currentSearch == null)
				return;

			var timer = Stopwatch.StartNew();
			int resultsAdded = 0;
			while (Results.Count < MAX_RESULTS && timer.ElapsedMilliseconds < MAX_REFRESH_TIME_MS && currentSearch.resultQueue.TryTake(out var result))
			{
				Results.InsertSorted(result, resultsComparer);
				++resultsAdded;
			}

			if (resultsAdded > 0 && Results.Count == MAX_RESULTS)
			{
				Results.Add(new SearchResult {
					Name = Properties.Resources.SearchAbortedMoreThan1000ResultsFound,
					Location = null!,
					Assembly = null!,
					Image = null!,
					LocationImage = null!,
					AssemblyImage = null!,
				});
				currentSearch.Cancel();
			}
		}

		async void StartSearch(string searchTerm)
		{
			if (currentSearch != null)
			{
				currentSearch.Cancel();
				currentSearch = null;
			}

			MainWindow mainWindow = MainWindow.Instance;
			resultsComparer = mainWindow.CurrentDisplaySettings.SortResults ?
				SearchResult.ComparerByFitness :
				SearchResult.ComparerByName;
			Results.Clear();

			RunningSearch startedSearch = null;
			if (!string.IsNullOrEmpty(searchTerm))
			{

				searchProgressBar.IsIndeterminate = true;
				startedSearch = new RunningSearch(await mainWindow.CurrentAssemblyList.GetAllAssemblies(), searchTerm,
					(SearchMode)searchModeComboBox.SelectedIndex, mainWindow.CurrentLanguage,
					filterSettings.ShowApiLevel);
				currentSearch = startedSearch;

				await startedSearch.Run();
			}

			if (currentSearch == startedSearch)
			{ //are we still running the same search
				searchProgressBar.IsIndeterminate = false;
			}
		}

		void JumpToSelectedItem()
		{
			if (listBox.SelectedItem is SearchResult result)
			{
				MainWindow.Instance.JumpToReference(result.Reference);
			}
		}

		sealed class RunningSearch
		{
			readonly CancellationTokenSource cts = new CancellationTokenSource();
			readonly IList<LoadedAssembly> assemblies;
			readonly SearchRequest searchRequest;
			readonly SearchMode searchMode;
			readonly Language language;
			readonly ApiVisibility apiVisibility;
			public readonly IProducerConsumerCollection<SearchResult> resultQueue = new ConcurrentQueue<SearchResult>();

			public RunningSearch(IList<LoadedAssembly> assemblies, string searchTerm, SearchMode searchMode,
				Language language, ApiVisibility apiVisibility)
			{
				this.assemblies = assemblies;
				this.language = language;
				this.searchMode = searchMode;
				this.apiVisibility = apiVisibility;
				this.searchRequest = Parse(searchTerm);
			}

			SearchRequest Parse(string input)
			{
				string[] parts = CommandLineTools.CommandLineToArgumentArray(input);

				SearchRequest request = new SearchRequest();
				List<string> keywords = new List<string>();
				Regex regex = null;
				request.Mode = searchMode;

				foreach (string part in parts)
				{
					// Parse: [prefix:|@]["]searchTerm["]
					// Find quotes used for escaping
					int prefixLength = part.IndexOfAny(new[] { '"', '/' });
					if (prefixLength < 0)
					{
						// no quotes
						prefixLength = part.Length;
					}

					int delimiterLength;
					// Find end of prefix
					if (part.StartsWith("@", StringComparison.Ordinal))
					{
						prefixLength = 1;
						delimiterLength = 0;
					}
					else
					{
						prefixLength = part.IndexOf(':', 0, prefixLength);
						delimiterLength = 1;
					}
					string prefix;
					if (prefixLength <= 0)
					{
						prefix = null;
						prefixLength = -1;
					}
					else
					{
						prefix = part.Substring(0, prefixLength);
					}

					// unescape quotes
					string searchTerm = part.Substring(prefixLength + delimiterLength).Trim();
					if (searchTerm.Length > 0)
					{
						searchTerm = CommandLineTools.CommandLineToArgumentArray(searchTerm)[0];
					}
					else
					{
						// if searchTerm is only "@" or "prefix:",
						// then we do not interpret it as prefix, but as searchTerm.
						searchTerm = part;
						prefix = null;
						prefixLength = -1;
					}

					if (prefix == null || prefix.Length <= 2)
					{
						if (regex == null && searchTerm.StartsWith("/", StringComparison.Ordinal) && searchTerm.Length > 1)
						{
							int searchTermLength = searchTerm.Length - 1;
							if (searchTerm.EndsWith("/", StringComparison.Ordinal))
							{
								searchTermLength--;
							}

							request.FullNameSearch |= searchTerm.Contains("\\.");

							regex = CreateRegex(searchTerm.Substring(1, searchTermLength));
						}
						else
						{
							request.FullNameSearch |= searchTerm.Contains(".");
							keywords.Add(searchTerm);
						}
						request.OmitGenerics |= !(searchTerm.Contains("<") || searchTerm.Contains("`"));
					}

					switch (prefix?.ToUpperInvariant())
					{
						case "@":
							request.Mode = SearchMode.Token;
							break;
						case "INNAMESPACE":
							request.InNamespace ??= searchTerm;
							break;
						case "INASSEMBLY":
							request.InAssembly ??= searchTerm;
							break;
						case "A":
							request.AssemblySearchKind = AssemblySearchKind.NameOrFileName;
							request.Mode = SearchMode.Assembly;
							break;
						case "AF":
							request.AssemblySearchKind = AssemblySearchKind.FilePath;
							request.Mode = SearchMode.Assembly;
							break;
						case "AN":
							request.AssemblySearchKind = AssemblySearchKind.FullName;
							request.Mode = SearchMode.Assembly;
							break;
						case "N":
							request.Mode = SearchMode.Namespace;
							break;
						case "TM":
							request.Mode = SearchMode.Member;
							request.MemberSearchKind = MemberSearchKind.All;
							break;
						case "T":
							request.Mode = SearchMode.Member;
							request.MemberSearchKind = MemberSearchKind.Type;
							break;
						case "M":
							request.Mode = SearchMode.Member;
							request.MemberSearchKind = MemberSearchKind.Member;
							break;
						case "MD":
							request.Mode = SearchMode.Member;
							request.MemberSearchKind = MemberSearchKind.Method;
							break;
						case "F":
							request.Mode = SearchMode.Member;
							request.MemberSearchKind = MemberSearchKind.Field;
							break;
						case "P":
							request.Mode = SearchMode.Member;
							request.MemberSearchKind = MemberSearchKind.Property;
							break;
						case "E":
							request.Mode = SearchMode.Member;
							request.MemberSearchKind = MemberSearchKind.Event;
							break;
						case "C":
							request.Mode = SearchMode.Literal;
							break;
						case "R":
							request.Mode = SearchMode.Resource;
							break;
					}
				}

				Regex CreateRegex(string s)
				{
					try
					{
						return new Regex(s, RegexOptions.Compiled);
					}
					catch (ArgumentException)
					{
						return null;
					}
				}

				request.Keywords = keywords.ToArray();
				request.RegEx = regex;
				request.SearchResultFactory = new SearchResultFactory(language);
				request.TreeNodeFactory = new TreeNodeFactory();
				request.DecompilerSettings = MainWindow.Instance.CurrentDecompilerSettings;

				return request;
			}

			public void Cancel()
			{
				cts.Cancel();
			}

			public async Task Run()
			{
				try
				{
					await Task.Factory.StartNew(() => {
						var searcher = GetSearchStrategy(searchRequest);
						if (searcher == null)
							return;
						try
						{
							foreach (var loadedAssembly in assemblies)
							{
								var module = loadedAssembly.GetMetadataFileOrNull();
								if (module == null)
									continue;
								searcher.Search(module, cts.Token);
							}
						}
						catch (OperationCanceledException)
						{
							// ignore cancellation
						}

					}, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current).ConfigureAwait(false);
				}
				catch (TaskCanceledException)
				{
					// ignore cancellation
				}
			}

			AbstractSearchStrategy GetSearchStrategy(SearchRequest request)
			{
				if (request.Keywords.Length == 0 && request.RegEx == null)
					return null;

				switch (request.Mode)
				{
					case SearchMode.TypeAndMember:
						return new MemberSearchStrategy(language, apiVisibility, request, resultQueue);
					case SearchMode.Type:
						return new MemberSearchStrategy(language, apiVisibility, request, resultQueue, MemberSearchKind.Type);
					case SearchMode.Member:
						return new MemberSearchStrategy(language, apiVisibility, request, resultQueue, request.MemberSearchKind);
					case SearchMode.Literal:
						return new LiteralSearchStrategy(language, apiVisibility, request, resultQueue);
					case SearchMode.Method:
						return new MemberSearchStrategy(language, apiVisibility, request, resultQueue, MemberSearchKind.Method);
					case SearchMode.Field:
						return new MemberSearchStrategy(language, apiVisibility, request, resultQueue, MemberSearchKind.Field);
					case SearchMode.Property:
						return new MemberSearchStrategy(language, apiVisibility, request, resultQueue, MemberSearchKind.Property);
					case SearchMode.Event:
						return new MemberSearchStrategy(language, apiVisibility, request, resultQueue, MemberSearchKind.Event);
					case SearchMode.Token:
						return new MetadataTokenSearchStrategy(language, apiVisibility, request, resultQueue);
					case SearchMode.Resource:
						return new ResourceSearchStrategy(apiVisibility, request, resultQueue);
					case SearchMode.Assembly:
						return new AssemblySearchStrategy(request, resultQueue, AssemblySearchKind.NameOrFileName);
					case SearchMode.Namespace:
						return new NamespaceSearchStrategy(request, resultQueue);
				}

				return null;
			}
		}
	}

	[ExportToolbarCommand(ToolTip = nameof(Properties.Resources.SearchCtrlShiftFOrCtrlE), ToolbarIcon = "Images/Search", ToolbarCategory = nameof(Properties.Resources.View), ToolbarOrder = 100)]
	sealed class ShowSearchCommand : CommandWrapper
	{
		public ShowSearchCommand()
			: base(NavigationCommands.Search)
		{
			NavigationCommands.Search.InputGestures.Clear();
			NavigationCommands.Search.InputGestures.Add(new KeyGesture(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
			NavigationCommands.Search.InputGestures.Add(new KeyGesture(Key.E, ModifierKeys.Control));
		}
	}
}