/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of LocalController <https://github.com/OpenChargingCloud/LocalController>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Reflection;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// One assembly this local controller is running, and the commit it was built from
    /// when its repository stamps one.
    /// </summary>
    /// <param name="Name">The assembly's simple name.</param>
    /// <param name="Version">Its assembly version, when it has one.</param>
    /// <param name="Repository">The repository it was built from, when stamped.</param>
    /// <param name="Commit">The commit it was built from, when stamped.</param>
    public readonly record struct LoadedAssembly(String   Name,
                                                 String?  Version,
                                                 String?  Repository,
                                                 String?  Commit)
    {

        /// <summary>
        /// Whether its repository records where it came from.
        /// </summary>
        public Boolean IsStamped
            => Commit is not null;

        /// <summary>
        /// Whether it was built from a tree with uncommitted changes, which is
        /// the first thing worth knowing about a build that misbehaves.
        /// </summary>
        public Boolean IsDirty
            => Commit?.EndsWith("-dirty", StringComparison.Ordinal) == true;

    }


    /// <summary>
    /// What this local controller was built from, read from the assemblies themselves.
    /// </summary>
    /// <remarks>
    /// Read here rather than handed in by a launcher, and that is the whole
    /// point: a launcher asks git at startup and describes the working tree as
    /// it is then, while this describes the tree each assembly was compiled
    /// from. Start without rebuilding after checking out something else and the
    /// two disagree - and a commit in a bug report that looks right and is
    /// wrong costs more than none at all.
    ///
    /// Nothing is listed by hand. An assembly carrying a GitCommit is one of
    /// ours, so a new library brings itself along and a library that stops
    /// being referenced leaves by itself. The list a launcher passes cannot do
    /// either: a launcher's list goes stale the moment a submodule is added or
    /// dropped, and nothing makes it fail loudly when it does.
    /// </remarks>
    public static class BuiltFrom
    {

        #region Data

        private static readonly Lazy<IReadOnlyList<LoadedAssembly>> assemblies = new (Collect);

        #endregion

        #region Properties

        /// <summary>
        /// Every assembly of ours this process has loaded, in name order.
        /// </summary>
        public static IReadOnlyList<LoadedAssembly> Assemblies
            => assemblies.Value;

        /// <summary>
        /// One line per repository: what it is called and the commit it was
        /// built from. Several assemblies may come from one repository - the
        /// ISO 15118 repository alone holds dozens - and they all carry the
        /// same commit, so the repository is the unit worth reporting.
        /// </summary>
        public static IEnumerable<LoadedAssembly> Repositories

            => Assemblies.
                   Where  (assembly => assembly.IsStamped).
                   GroupBy(assembly => assembly.Repository!).
                   Select (group    => group.First()).
                   OrderBy(assembly => assembly.Repository, StringComparer.OrdinalIgnoreCase);

        #endregion


        #region (private static) Collect()

        private static IReadOnlyList<LoadedAssembly> Collect()
        {

            var found  = new Dictionary<String, LoadedAssembly>(StringComparer.Ordinal);
            var queue  = new Queue<Assembly>();

            // Both ends: the entry assembly is the command line tool, which the
            // controller does not reference and would otherwise be missing from
            // its own report; this assembly is where everything else hangs off,
            // and is what a test host or another host would start from instead.
            foreach (var start in new[] { Assembly.GetEntryAssembly(), typeof(BuiltFrom).Assembly })
                if (start is not null)
                    queue.Enqueue(start);

            while (queue.Count > 0)
            {

                var assembly  = queue.Dequeue();
                var name      = assembly.GetName();

                if (name.Name is null || !found.TryAdd(name.Name, Read(assembly, name)))
                    continue;

                foreach (var reference in assembly.GetReferencedAssemblies())
                {

                    if (reference.Name is null ||
                        IsPlatform(reference.Name)  ||
                        found.ContainsKey(reference.Name))
                    {
                        continue;
                    }

                    // Loading is what makes this complete rather than a snapshot
                    // of whatever happened to be touched first: .NET loads lazily,
                    // so asking the AppDomain at startup gives a different answer
                    // depending on how the controller was started.
                    try
                    {
                        queue.Enqueue(Assembly.Load(reference));
                    }
                    catch
                    {
                        // A reference that cannot be resolved is one this process
                        // never uses. It is not worth failing a bug report over.
                    }

                }

            }

            return [.. found.Values.OrderBy(assembly => assembly.Name, StringComparer.OrdinalIgnoreCase)];

        }

        #endregion

        #region (private static) Read       (Assembly, Name)

        private static LoadedAssembly Read(Assembly Assembly, AssemblyName Name)
        {

            String? Metadata(String Key)
                => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().
                            FirstOrDefault(attribute => attribute.Key == Key)?.
                            Value is String value && value.Length > 0
                       ? value
                       : null;

            return new LoadedAssembly(
                       Name.Name ?? "",
                       Name.Version?.ToString(3),
                       Metadata("GitRepository"),
                       Metadata("GitCommit")
                   );

        }

        #endregion

        #region (private static) IsPlatform (Name)

        /// <summary>
        /// Whether a reference belongs to the runtime rather than to us.
        /// </summary>
        /// <remarks>
        /// Only to avoid loading half the base class library at startup for
        /// nothing. It decides what is worth walking, never what is worth
        /// reporting - the stamp decides that, and a stamped assembly would be
        /// reported whatever it were called.
        /// </remarks>
        private static Boolean IsPlatform(String Name)

            => Name.StartsWith("System.",    StringComparison.Ordinal) ||
               Name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
               Name == "System"                                        ||
               Name == "mscorlib"                                      ||
               Name == "netstandard";

        #endregion

    }

}
