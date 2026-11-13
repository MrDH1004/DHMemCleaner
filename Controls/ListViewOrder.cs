using System.Collections.Generic;
using System.Windows.Forms;

namespace MemoryCleaner.Controls
{
    // 증분 갱신 ListView의 행 순서를 데이터 순서(정렬 기준)와 맞춘다.
    // 항목 객체를 재사용하므로 텍스트·색은 유지되고, 선택/스크롤 복원은 호출부가 한다.
    internal static class ListViewOrder
    {
        // 현재 행 순서가 desired와 다르면(새 항목·순위 변동·사라진 항목) 재배치한다.
        // BeginUpdate/EndUpdate 안에서 호출할 것.
        public static void Apply(ListView list, List<ListViewItem> desired)
        {
            if (SameOrder(list.Items, desired)) return;
            list.Items.Clear();
            list.Items.AddRange(desired.ToArray());
        }

        private static bool SameOrder(ListView.ListViewItemCollection current, List<ListViewItem> desired)
        {
            if (current.Count != desired.Count) return false;
            for (int i = 0; i < desired.Count; i++)
                if (!ReferenceEquals(current[i], desired[i])) return false;
            return true;
        }
    }
}
