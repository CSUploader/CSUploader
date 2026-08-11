// <copyright file="UploadTreeNode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CSUploader.ViewModels;

/// <summary>
/// One node of the upload wizard's source tree: the "All files" root, a folder that was added, a
/// folder inside one, or the bucket individually-picked files land in.
/// <para>
/// The tree exists because the flat list it replaced could not answer "what am I actually uploading"
/// once a package drew from several places — and because the strip that listed the sources sat ABOVE
/// the grid, so every folder added cost the file list height. A column costs it none.
/// </para>
/// <para>
/// Nodes hold no files of their own beyond <see cref="OwnFiles"/> (the ones directly inside that
/// folder); everything else is derived by walking, so the tree can be rebuilt from
/// <c>UploadWizardViewModel.Files</c> whenever it changes without any state to keep in step.
/// </para>
/// </summary>
public sealed partial class UploadTreeNode : ObservableObject
{
    public UploadTreeNode(string name, UploadTreeNodeKind kind, UploadSource? source = null)
    {
        Name = name;
        Kind = kind;
        Source = source;
    }

    /// <summary>What the row shows — a folder's own name, or the localized label of a special node.</summary>
    public string Name { get; }

    public UploadTreeNodeKind Kind { get; }

    /// <summary>
    /// The source this node is the root of, when it is one. Only a root carries it, and only a root
    /// can be removed — that is what the tree's remove affordance acts on.
    /// </summary>
    public UploadSource? Source { get; }

    /// <summary>True for a node the user can remove: a source's own root.</summary>
    public bool IsRemovable => Source is not null;

    public ObservableCollection<UploadTreeNode> Children { get; } = [];

    /// <summary>Files directly inside this folder — not those in its subfolders.</summary>
    public List<FileEntry> OwnFiles { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>
    /// Every file in this node and everything beneath it, which is what the grid shows when the node
    /// is selected — a source root therefore shows the whole source rather than only its top level.
    /// </summary>
    public IEnumerable<FileEntry> AllFiles()
    {
        foreach (FileEntry file in OwnFiles)
        {
            yield return file;
        }

        foreach (UploadTreeNode child in Children)
        {
            foreach (FileEntry file in child.AllFiles())
            {
                yield return file;
            }
        }
    }

    /// <summary>How many files this node covers — the count shown beside its name.</summary>
    public int FileCount => OwnFiles.Count + Children.Sum(c => c.FileCount);

    /// <summary>
    /// Tri-state tick: true when every file beneath is ticked, false when none is, null when some are.
    /// Setting it ticks or unticks the whole subtree, which is the point — excluding one subfolder of
    /// two hundred files should not mean two hundred clicks in the grid.
    /// <para>
    /// The setter deliberately ignores an incoming null. A three-state <c>CheckBox</c> cycles
    /// checked → unchecked → INDETERMINATE, and "make this branch partially ticked" has no meaning;
    /// letting the cycle write null would silently do nothing while looking like it did something.
    /// </para>
    /// </summary>
    public bool? IsChecked
    {
        get
        {
            bool anyTicked = false;
            bool anyUnticked = false;
            foreach (FileEntry file in AllFiles())
            {
                if (file.IsSelected)
                {
                    anyTicked = true;
                }
                else
                {
                    anyUnticked = true;
                }

                if (anyTicked && anyUnticked)
                {
                    return null;
                }
            }

            // An empty node reads as unticked rather than "all of nothing is ticked".
            return anyTicked;
        }

        set
        {
            if (value is not bool ticked)
            {
                return;
            }

            foreach (FileEntry file in AllFiles())
            {
                file.IsSelected = ticked;
            }
        }
    }

    /// <summary>
    /// Re-reads <see cref="IsChecked"/> and <see cref="FileCount"/> here and on every ancestor —
    /// called when a file's tick changes, since a leaf toggle can flip a whole chain of parents from
    /// partial to full and back.
    /// </summary>
    public void RefreshCheckState()
    {
        RefreshCheckStateLocal();
        Parent?.RefreshCheckState();
    }

    /// <summary>Re-reads this node only — for a caller already walking the whole tree, where climbing
    /// to the root from every node would revisit the upper levels once per descendant.</summary>
    public void RefreshCheckStateLocal()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(FileCount));
    }

    /// <summary>Set while the tree is built; the chain is what lets a leaf toggle reach the root.</summary>
    public UploadTreeNode? Parent { get; internal set; }

    /// <summary>Adds a child and links it back, so ancestors stay reachable.</summary>
    internal void AddChild(UploadTreeNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }
}

/// <summary>What a <see cref="UploadTreeNode"/> stands for — the row's icon and whether it can be
/// removed follow from it.</summary>
public enum UploadTreeNodeKind
{
    /// <summary>The single root: everything in the package.</summary>
    All,

    /// <summary>A folder the user added, or one inside it.</summary>
    Folder,

    /// <summary>Where individually-picked files live, since they have no folder of their own here.</summary>
    LooseFiles,
}
