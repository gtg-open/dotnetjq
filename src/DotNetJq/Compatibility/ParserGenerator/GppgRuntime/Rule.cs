#nullable disable
// Source-integrated from springcomp/gppg 1.2.5 commit f4634057620757a38789b4d53df73817667d1842 (Rule.cs).
// Modifications: the top-level runtime type is internal to DotNetJq.dll;
// the pinned source's nullable context is retained explicitly.
// Gardens Point Parser Generator
// Copyright (c) Wayne Kelly, QUT 2005-2010
// (see accompanying GPPGcopyright.rtf)

namespace StarodubOleg.GPPG.Runtime
{
	/// <summary>
	/// Rule representation at runtime.
	/// </summary>
	internal class Rule
	{
		internal int LeftHandSide; // symbol
		internal int[] RightHandSide; // symbols

		/// <summary>
		/// Rule constructor.  This holds the ordinal of
		/// the left hand side symbol, and the list of
		/// right hand side symbols, in lexical order.
		/// </summary>
		/// <param name="left">The LHS non-terminal</param>
		/// <param name="right">The RHS symbols, in lexical order</param>
		public Rule(int left, int[] right)
		{
			this.LeftHandSide = left;
			this.RightHandSide = right;
		}
	}
}
