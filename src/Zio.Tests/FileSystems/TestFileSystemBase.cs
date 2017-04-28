using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using Zio.FileSystems;

namespace Zio.Tests.FileSystems
{
    public abstract class TestFileSystemBase : IDisposable
    {
        private static readonly UPath[] Directories = new UPath[] { "a", "b", "C", "d" };
        private static readonly UPath[] Files = new UPath[] { "b.txt", "c.txt1", "d.i", "f.i1", "A.txt", "a/a.txt", "b/b.i", "E" };
        private static readonly object Lock = new object();
        private PhysicalDirectoryHelper _physicalDirectoryHelper;
        private readonly EnumeratePathsResult _referenceEnumeratePathsResult;


        // -------------------------------------
        // This creates the following FileSystem
        // -------------------------------------
        // /a
        //     /a
        //        a.txt
        //     /b
        //        b.i
        //     /C
        //     /d
        //     a.txt
        //     A.txt
        //     b.txt
        //     c.txt1
        //     d.i
        //     f.i1
        //     E
        // /b
        //    b.i
        // /C
        // /d
        // A.txt
        // b.txt
        // c.txt1
        // d.i
        // f.i1
        // E

        protected TestFileSystemBase()
        {
            SystemPath = Path.GetDirectoryName(typeof(TestFileSystemBase).GetTypeInfo().Assembly.Location);
            IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            // Use a static lock to make sure a single process is running
            // as we may have changed on the disk that may interact with other tests
            Monitor.Enter(Lock);

            _referenceEnumeratePathsResult = new EnumeratePathsResult(GetCommonPhysicalFileSystem());
        }

        public string SystemPath { get; }

        public bool IsWindows { get; }

        protected static void SafeDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception)
            {
            }
        }
        protected static void SafeDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        public virtual void Dispose()
        {
            if (_physicalDirectoryHelper != null)
            {
                _physicalDirectoryHelper.Dispose();
            }

            Monitor.Exit(Lock);
        }


        protected IFileSystem GetCommonPhysicalFileSystem()
        {
            if (_physicalDirectoryHelper == null)
            {
                _physicalDirectoryHelper = new PhysicalDirectoryHelper(SystemPath);
                CreateFolderStructure(_physicalDirectoryHelper.PhysicalFileSystem);
            }
            return _physicalDirectoryHelper.PhysicalFileSystem;
        }

        protected MemoryFileSystem GetCommonMemoryFileSystem()
        {
            var fs = new MemoryFileSystem();
            CreateFolderStructure(fs);
            return fs;
        }

        protected AggregateFileSystem GetCommonAggregateFileSystem()
        {
            // ----------------------------------------------
            // This creates the following AggregateFileSystem
            // ----------------------------------------------
            // /a                 -> fs2
            //     /a             -> fs1
            //        a.txt       -> fs1
            //     /b             -> fs1
            //        b.i         -> fs1
            //     /C             -> fs2
            //     /d             -> fs2
            //     a.txt          -> fs2
            //     A.txt          -> fs2
            //     b.txt          -> fs2
            //     c.txt1         -> fs2
            //     d.i            -> fs2
            //     f.i1           -> fs2
            //     E              -> fs2
            // /b                 -> fs1
            //    b.i             -> fs1
            // /C                 -> fs2
            // /d                 -> fs3
            // A.txt              -> fs3
            // b.txt              -> fs2
            // c.txt1             -> fs3
            // d.i                -> fs3
            // f.i1               -> fs3
            // E                  -> fs2

            var fs1 = new MemoryFileSystem();
            CreateFolderStructure(fs1);
            var fs2 = fs1.Clone();
            var fs3 = fs2.Clone();

            // Delete part of fs2 so that it will fallback to fs1
            fs2.DeleteDirectory("/a/a", true);
            fs2.DeleteDirectory("/a/b", true);
            fs2.DeleteDirectory("/b", true);

            // Delete on fs3 to fallback to fs2 and fs1
            fs3.DeleteDirectory("/a", true);
            fs3.DeleteDirectory("/C", true);
            fs3.DeleteFile("/b.txt");
            fs3.DeleteFile("/E");

            var aggfs = new AggregateFileSystem(fs1);
            aggfs.AddFileSystem(fs2);
            aggfs.AddFileSystem(fs3);

            return aggfs;
        }

        protected MountFileSystem GetCommonMountFileSystemWithOnlyBackup()
        {
            // Check on MountFileSystem directly with backup mount
            var fs = new MemoryFileSystem();
            CreateFolderStructure(fs);
            var mountfs = new MountFileSystem(fs);
            return mountfs;
        }

        protected MountFileSystem GetCommonMountFileSystemWithMounts()
        {
            // Check on MountFileSystem
            // with real mount
            var fs = new MemoryFileSystem();
            CreateFolderStructure(fs);
            fs.DeleteDirectory("/b", true);
            fs.DeleteDirectory("/C", true);

            var fs1 = new MemoryFileSystem();
            fs1.WriteAllText("/b.i", "content");

            var mountfs = new MountFileSystem(fs);
            mountfs.Mount("/b", fs1);
            mountfs.Mount("/C", new MemoryFileSystem());

            return mountfs;
        }

        protected void AssertCommonReadOnly(IFileSystem fs)
        {
            Assert.True(fs.DirectoryExists("/"));

            Assert.Throws<IOException>(() => fs.CreateDirectory("/test"));
            Assert.Throws<IOException>(() => fs.DeleteDirectory("/test", true));
            Assert.Throws<IOException>(() => fs.MoveDirectory("/drive", "/drive2"));

            Assert.Throws<IOException>(() => fs.CreateFile("/toto.txt"));
            Assert.Throws<IOException>(() => fs.CopyFile("/toto.txt", "/dest.txt", true));
            Assert.Throws<IOException>(() => fs.MoveFile("/drive", "/drive2"));
            Assert.Throws<IOException>(() => fs.DeleteFile("/toto.txt"));
            Assert.Throws<IOException>(() => fs.OpenFile("/toto.txt", FileMode.Create, FileAccess.ReadWrite));
            Assert.Throws<IOException>(() => fs.OpenFile("/toto.txt", FileMode.Open, FileAccess.Write));
            Assert.Throws<IOException>(() => fs.ReplaceFile("/a/a/a.txt", "/A.txt", "/titi.txt", true));

            Assert.Throws<IOException>(() => fs.SetAttributes("/toto.txt", FileAttributes.ReadOnly));
            Assert.Throws<IOException>(() => fs.SetCreationTime("/toto.txt", DateTime.Now));
            Assert.Throws<IOException>(() => fs.SetLastAccessTime("/toto.txt", DateTime.Now));
            Assert.Throws<IOException>(() => fs.SetLastWriteTime("/toto.txt", DateTime.Now));

            AssertCommonRead(fs, true);
        }

        protected void AssertCommonRead(IFileSystem fs, bool isReadOnly = false)
        {
            Assert.True(fs.DirectoryExists("/"));

            Assert.True(fs.FileExists("/A.txt"));
            Assert.True(fs.FileExists("/b.txt"));
            Assert.True(fs.FileExists("/b/b.i"));
            Assert.True(fs.FileExists("/a/a/a.txt"));
            Assert.False(fs.FileExists("/yoyo.txt"));

            Assert.True(fs.DirectoryExists("/a"));
            Assert.True(fs.DirectoryExists("/a/b"));
            Assert.True(fs.DirectoryExists("/a/C"));
            Assert.True(fs.DirectoryExists("/b"));
            Assert.True(fs.DirectoryExists("/C"));
            Assert.True(fs.DirectoryExists("/d"));
            Assert.False(fs.DirectoryExists("/yoyo"));
            Assert.False(fs.DirectoryExists("/a/yoyo"));

            Assert.StartsWith("content", fs.ReadAllText("/A.txt"));
            Assert.StartsWith("content", fs.ReadAllText("/b.txt"));
            Assert.StartsWith("content", fs.ReadAllText("/a/a/a.txt"));


            var readOnlyFlag = isReadOnly ? FileAttributes.ReadOnly : 0;

            Assert.Equal(readOnlyFlag | FileAttributes.Archive, fs.GetAttributes("/A.txt"));
            Assert.Equal(readOnlyFlag | FileAttributes.Archive, fs.GetAttributes("/b.txt"));
            Assert.Equal(readOnlyFlag | FileAttributes.Archive, fs.GetAttributes("/a/a/a.txt"));

            Assert.Equal(readOnlyFlag | FileAttributes.Directory, fs.GetAttributes("/a"));
            Assert.Equal(readOnlyFlag | FileAttributes.Directory, fs.GetAttributes("/a/a"));
            Assert.Equal(readOnlyFlag | FileAttributes.Directory, fs.GetAttributes("/C"));
            Assert.Equal(readOnlyFlag | FileAttributes.Directory, fs.GetAttributes("/d"));

            Assert.NotEqual(FileSystem.DefaultFileTime, fs.GetCreationTime("/A.txt"));
            Assert.NotEqual(FileSystem.DefaultFileTime, fs.GetLastAccessTime("/A.txt"));
            Assert.NotEqual(FileSystem.DefaultFileTime, fs.GetLastWriteTime("/A.txt"));
            Assert.NotEqual(FileSystem.DefaultFileTime, fs.GetCreationTime("/a/a/a.txt"));
            Assert.NotEqual(FileSystem.DefaultFileTime, fs.GetLastAccessTime("/a/a/a.txt"));
            Assert.NotEqual(FileSystem.DefaultFileTime, fs.GetLastWriteTime("/a/a/a.txt"));

            new EnumeratePathsResult(fs).Check(_referenceEnumeratePathsResult);
        }


        [SuppressMessage("ReSharper", "InconsistentNaming")]
        class EnumeratePathsResult
        {
            private readonly List<UPath> TopDirs;
            private readonly List<UPath> TopFiles;
            private readonly List<UPath> TopEntries;
            private readonly List<UPath> AllDirs;
            private readonly List<UPath> AllFiles;
            private readonly List<UPath> AllEntries;
            private readonly List<UPath> AllFiles_txt;
            private readonly List<UPath> AllDirs_a1;
            private readonly List<UPath> AllDirs_a2;
            private readonly List<UPath> AllFiles_i;
            private readonly List<UPath> AllEntries_b;

            public void Check(EnumeratePathsResult other)
            {
                Assert.Equal(TopDirs, other.TopDirs);
                Assert.Equal(TopFiles, other.TopFiles);
                Assert.Equal(TopEntries, other.TopEntries);

                Assert.Equal(AllDirs, other.AllDirs);
                Assert.Equal(AllFiles, other.AllFiles);
                Assert.Equal(AllEntries, other.AllEntries);

                Assert.Equal(AllFiles_txt, other.AllFiles_txt);
                Assert.Equal(AllFiles_i, other.AllFiles_i);
                Assert.Equal(AllEntries_b, other.AllEntries_b);
                Assert.Equal(AllDirs_a1, other.AllDirs_a1);
                Assert.Equal(AllDirs_a2, other.AllDirs_a2);
            }

            public EnumeratePathsResult(IFileSystem fs)
            {
                TopDirs = fs.EnumeratePaths("/", "*", SearchOption.TopDirectoryOnly, SearchTarget.Directory).ToList();
                TopFiles = fs.EnumeratePaths("/", "*", SearchOption.TopDirectoryOnly, SearchTarget.File).ToList();
                TopEntries = fs.EnumeratePaths("/", "*", SearchOption.TopDirectoryOnly, SearchTarget.Both).ToList();

                AllDirs = fs.EnumeratePaths("/", "*", SearchOption.AllDirectories, SearchTarget.Directory).ToList();
                AllFiles = fs.EnumeratePaths("/", "*", SearchOption.AllDirectories, SearchTarget.File).ToList();
                AllEntries = fs.EnumeratePaths("/", "*", SearchOption.AllDirectories, SearchTarget.Both).ToList();

                AllFiles_txt = fs.EnumeratePaths("/", "*.txt", SearchOption.AllDirectories, SearchTarget.File).ToList();
                AllDirs_a1 = fs.EnumeratePaths("/", "a/*", SearchOption.AllDirectories, SearchTarget.Directory).ToList();
                AllDirs_a2 = fs.EnumeratePaths("/a", "*", SearchOption.AllDirectories, SearchTarget.Directory).ToList();
                AllFiles_i = fs.EnumeratePaths("/", "*.i", SearchOption.AllDirectories, SearchTarget.File).ToList();
                AllEntries_b = fs.EnumeratePaths("/", "b*", SearchOption.AllDirectories, SearchTarget.Both).ToList();
            }
        }

        private void CreateFolderStructure(IFileSystem fs)
        {
            void CreateFolderStructure(UPath root)
            {

                foreach (var dir in Directories)
                {
                    var pathDir = root / dir;
                    fs.CreateDirectory(pathDir);
                }

                for (var i = 0; i < Files.Length; i++)
                {
                    var file = Files[i];
                    var pathFile = root / file;
                    fs.WriteAllText(pathFile, "content" + i);
                }
            }

            CreateFolderStructure(UPath.Root);
            CreateFolderStructure(UPath.Root / "a");
        }
    }
}