namespace PhotoBook.Engine;

/// <summary>
/// The exact O(n³) Hungarian (Jonker–Volgenant style, shortest augmenting paths with dual
/// potentials) used by phase 5 of doc 08. With n ≤ 8 slots per page this is ~512 operations, so
/// exactness is free — and it avoids the classic greedy failure where the first photo takes the
/// slot the last photo needed. Deterministic: every scan runs in index order and ties keep the
/// first candidate, so the same matrix always yields the same assignment.
/// </summary>
public static class Hungarian
{
    /// <summary>
    /// Solves the rectangular-free (square) assignment problem: returns, for each row, the column
    /// it is assigned to, minimizing the total cost.
    /// </summary>
    /// <param name="cost">A square cost matrix; <c>cost[row, column]</c>.</param>
    /// <exception cref="ArgumentException">The matrix is not square.</exception>
    public static int[] Solve(double[,] cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        var n = cost.GetLength(0);
        if (cost.GetLength(1) != n)
        {
            throw new ArgumentException("The assignment cost matrix must be square.", nameof(cost));
        }

        if (n == 0) return [];
        if (n == 1) return [0];

        var u = new double[n + 1];          // dual potentials, rows
        var v = new double[n + 1];          // dual potentials, columns
        var p = new int[n + 1];             // p[column] = row matched to it (1-based; 0 = free)
        var way = new int[n + 1];           // alternating-path predecessor

        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = new double[n + 1];
            var used = new bool[n + 1];
            for (var j = 0; j <= n; j++) minv[j] = double.PositiveInfinity;

            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = double.PositiveInfinity;
                var j1 = -1;

                for (var j = 1; j <= n; j++)
                {
                    if (used[j]) continue;
                    var current = cost[i0 - 1, j - 1] - u[i0] - v[j];
                    if (current < minv[j])
                    {
                        minv[j] = current;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                if (j1 < 0) break;          // unreachable for a finite matrix; guards against NaN input

                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        var assignment = new int[n];
        for (var i = 0; i < n; i++) assignment[i] = -1;
        for (var j = 1; j <= n; j++)
        {
            if (p[j] > 0) assignment[p[j] - 1] = j - 1;
        }

        // Any row left unmatched (only possible with NaN costs) takes the first free column, so the
        // engine degrades to a valid permutation instead of throwing.
        var taken = new bool[n];
        foreach (var column in assignment)
        {
            if (column >= 0) taken[column] = true;
        }

        for (var i = 0; i < n; i++)
        {
            if (assignment[i] >= 0) continue;
            for (var j = 0; j < n; j++)
            {
                if (taken[j]) continue;
                assignment[i] = j;
                taken[j] = true;
                break;
            }
        }

        return assignment;
    }

    /// <summary>The total cost of an assignment produced by <see cref="Solve"/>.</summary>
    public static double TotalCost(double[,] cost, int[] assignment)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(assignment);
        var total = 0.0;
        for (var i = 0; i < assignment.Length; i++)
        {
            if (assignment[i] >= 0) total += cost[i, assignment[i]];
        }

        return total;
    }
}
