/*
	Copyright (c) 2015 Ki

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
	THE SOFTWARE.
*/

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Xml.Linq;
using dnlib.DotNet;
using dnSpy.BamlDecompiler.Baml;
using dnSpy.BamlDecompiler.Xaml;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.BamlDecompiler {
	internal class XamlContext {
		XamlContext(ModuleDef module) {
			Module = module;
			NodeMap = new Dictionary<BamlRecord, BamlBlockNode>();
			XmlNs = new XmlnsDictionary();
		}

		Dictionary<ushort, XamlType> typeMap = new Dictionary<ushort, XamlType>();
		Dictionary<ushort, XamlProperty> propertyMap = new Dictionary<ushort, XamlProperty>();
		Dictionary<string, XNamespace> xmlnsMap = new Dictionary<string, XNamespace>();

		public ModuleDef Module { get; }
		public CancellationToken CancellationToken { get; private set; }
		public BamlDecompilerOptions BamlDecompilerOptions { get; private set; }

		public BamlContext Baml { get; private set; }
		public BamlNode RootNode { get; private set; }
		public IDictionary<BamlRecord, BamlBlockNode> NodeMap { get; }

		public XmlnsDictionary XmlNs { get; }

		public static XamlContext Construct(ModuleDef module, BamlDocument document, CancellationToken token, BamlDecompilerOptions bamlDecompilerOptions) {
			var ctx = new XamlContext(module);
			ctx.CancellationToken = token;
			ctx.BamlDecompilerOptions = bamlDecompilerOptions ?? BamlDecompilerOptions.CreateCSharp();

			ctx.Baml = BamlContext.ConstructContext(module, document, token);
			ctx.RootNode = BamlNode.Parse(document, token);

			ctx.BuildPIMappings(document);
			ctx.BuildNodeMap(ctx.RootNode as BamlBlockNode, new RecursionCounter());

			return ctx;
		}

		void BuildNodeMap(BamlBlockNode node, RecursionCounter counter) {
			if (node is null || !counter.Increment())
				return;

			NodeMap[node.Header] = node;

			foreach (var child in node.Children) {
				if (child is BamlBlockNode childBlock)
					BuildNodeMap(childBlock, counter);
			}

			counter.Decrement();
		}

		void BuildPIMappings(BamlDocument document) {
			foreach (var record in document) {
				var piMap = record as PIMappingRecord;
				if (piMap is null)
					continue;

				XmlNs.SetPIMapping(piMap.XmlNamespace, piMap.ClrNamespace, Baml.ResolveAssembly(piMap.AssemblyId));
			}
		}

		class DummyAssemblyRefFinder : IAssemblyRefFinder {
			readonly IAssembly assemblyDef;

			public DummyAssemblyRefFinder(IAssembly assemblyDef) => this.assemblyDef = assemblyDef;

			public AssemblyRef FindAssemblyRef(TypeRef nonNestedTypeRef) => assemblyDef.ToAssemblyRef();
		}

		public XamlType ResolveType(ushort id) {
			if (typeMap.TryGetValue(id, out var xamlType))
				return xamlType;

			ITypeDefOrRef type;
			IAssembly assembly;

			if (id > 0x7fff) {
				type = Baml.KnownThings.Types((KnownTypes)(-id));
				assembly = type.DefinitionAssembly;
			}
			else {
				var typeRec = Baml.TypeIdMap[id];
				assembly = Baml.ResolveAssembly(typeRec.AssemblyId);
				type = TypeNameParser.ParseReflectionThrow(Module, typeRec.TypeFullName, new DummyAssemblyRefFinder(assembly));
			}

			var clrNs = type.ReflectionNamespace;
			var xmlNs = XmlNs.LookupXmlns(assembly, clrNs);

			typeMap[id] = xamlType = new XamlType(assembly, clrNs, type.ReflectionName, GetXmlNamespace(xmlNs)) {
				ResolvedType = type
			};

			return xamlType;
		}

		public XamlProperty ResolveProperty(ushort id) {
			if (propertyMap.TryGetValue(id, out var xamlProp))
				return xamlProp;

			XamlType type;
			string name;
			IMemberDef member;

			if (id > 0x7fff) {
				var knownProp = Baml.KnownThings.Members((KnownMembers)(-id));
				type = ResolveType((ushort)-(short)knownProp.Parent);
				name = knownProp.Name;
				member = knownProp.Property;
			}
			else {
				var attrRec = Baml.AttributeIdMap[id];
				type = ResolveType(attrRec.OwnerTypeId);
				name = attrRec.Name;

				member = null;
			}

			propertyMap[id] = xamlProp = new XamlProperty(type, name) {
				ResolvedMember = member
			};
			xamlProp.TryResolve();

			return xamlProp;
		}

		public string ResolveString(ushort id) {
			if (id > 0x7fff)
				return Baml.KnownThings.Strings((short)-id);
			else if (Baml.StringIdMap.ContainsKey(id))
				return Baml.StringIdMap[id].Value;

			return null;
		}

		public XNamespace GetXmlNamespace(string xmlns) {
			if (xmlns is null)
				return null;

			if (!xmlnsMap.TryGetValue(xmlns, out var ns))
				xmlnsMap[xmlns] = ns = XNamespace.Get(xmlns);
			return ns;
		}

		public string TryGetXmlNamespace(IAssembly assembly, string typeNamespace) {
			var asm = assembly as AssemblyDef;
			if (asm is null)
				return null;

			foreach (var attr in asm.CustomAttributes.FindAll("System.Windows.Markup.XmlnsDefinitionAttribute")) {
				Debug.Assert(attr.ConstructorArguments.Count == 2);
				if (attr.ConstructorArguments.Count != 2)
					continue;
				var xmlNs = attr.ConstructorArguments[0].Value as UTF8String;
				var typeNs = attr.ConstructorArguments[1].Value as UTF8String;
				Debug2.Assert(!(xmlNs is null) && !(typeNs is null));
				if (xmlNs is null || typeNs is null)
					continue;

				if (typeNamespace == typeNs.String)
					return xmlNs;
			}

			return null;
		}

		public XName GetXamlNsName(string name, XElement elem = null) {
			var xNs = GetXmlNamespace("http://schemas.microsoft.com/winfx/2006/xaml");
			XName xName;
			if (!(elem is null) && xNs == elem.GetDefaultNamespace())
				xName = name;
			else
				xName = xNs + name;
			return xName;
		}

		public XName GetPseudoName(string name) => XNamespace.Get("https://github.com/0xd4d/dnSpy").GetName(name);
	}
}
