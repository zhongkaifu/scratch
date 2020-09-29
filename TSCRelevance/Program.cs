using CRFSharp;
using CRFSharpWrapper;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Schema;

namespace TSCRelevance
{
    public class Interval
    {
        public string uid { get; set; }

        public int start { get; set; }
        public int end { get; set; }

        public string text { get; set; }
    }

    public class Speaker
    {
        public int start { get; set; }
        public int end { get; set; }

        public string kind { get; set; }

        public List<Interval> intervals { get; set; }
    }

    public class Content
    {
        public int start { get; set; }
        public int end { get; set; }

        public List<Speaker> speakers { get; set; }
    }


    public class Transcript
    {
        public string encounter { get; set; }

        public string version { get; set; }

        public Content content { get; set; }
    }

    public class Turn
    {
        public string role { get; set; }

        public string text { get; set; }
    }

    public class QueryTermProp
    {
        public int termId;
        public int offset;
        public int synType;
        public int synSrcTerm;
    }

    public class IndexedSent
    {
        public string Sent;
        public string Section; //Saved to file
        public List<int> WordIds; //Saved to file
        public string Id;
        public string Offset;
    }


    class Program
    {
        public static int m_maxWordId = 0;
        public static ConcurrentDictionary<string, int> m_word2id = new ConcurrentDictionary<string, int>();
        public static List<string> m_wordId2word = new List<string>();


        public static int m_maxSentId = 0;
        public static List<IndexedSent> m_sentId2Sent = new List<IndexedSent>();
        public static List<List<int>> m_wordId2SentId = new List<List<int>>();
        private static int iRankLevelFactor = 100000;

        public static string vocabFilePath = "vocab.bin";
        public static string sentFilePath = "sents.bin";
        public static string indexFilePath = "index.bin";


        public static string SplitPunc(string str)
        {
            List<string> results = new List<string>();
            string[] words = str.Split(" ", StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < words.Length; i++)
            {
                char lastCh = words[i][words[i].Length - 1];
                if (Char.IsPunctuation(lastCh))
                {
                    if (words[i].Length > 1)
                    {
                        results.Add(words[i].Substring(0, words[i].Length - 1));
                    }
                    results.Add(lastCh.ToString());
                }
                else
                {
                    results.Add(words[i]);
                }

            }

            return String.Join(" ", results);
        }

        public static Dictionary<string, string> LoadPrelabeledFile(string prelabeledFilePath)
        {
            Dictionary<string, string> results = new Dictionary<string, string>();
            string[] lines = File.ReadAllLines(prelabeledFilePath);

            foreach (var line in lines)
            {
                string[] items = line.Split('\t');
                string keyword = items[1];
                string topCategory = items[2];
                string[] subItems = topCategory.Split(' ');
                string sectionName = subItems[0];

                results.Add(keyword, sectionName);
            }

            return results;
        }

       


       


        public static void LoadIndex(string rootPath)
        {
            //Load vocab
            StreamReader srVocab = new StreamReader(System.IO.Path.Combine(rootPath, vocabFilePath));
            m_maxWordId = int.Parse(srVocab.ReadLine());

            for (int i = 0; i < m_maxWordId; i++)
            {
                string word = srVocab.ReadLine();

                m_word2id.TryAdd(word, i);
                m_wordId2word.Add(word);
                m_wordId2SentId.Add(new List<int>());
            }
            srVocab.Close();

            //Read sent and its id
            StreamReader srSents = new StreamReader(System.IO.Path.Combine(rootPath, sentFilePath));
            m_maxSentId = int.Parse(srSents.ReadLine());

            for (int i = 0; i < m_maxSentId; i++)
            {
                IndexedSent ns = new IndexedSent();

                ns.Id = srSents.ReadLine();
                ns.Section = srSents.ReadLine();
                ns.Offset = srSents.ReadLine();

                string words = srSents.ReadLine();
                string[] items = words.Split(' ');
                ns.WordIds = new List<int>();
                foreach (var item in items)
                {
                    ns.WordIds.Add(int.Parse(item));
                }

                m_sentId2Sent.Add(ns);
            }

            srSents.Close();

            //Read index (wordid to sentId list)
            StreamReader srIndex = new StreamReader(System.IO.Path.Combine(rootPath, indexFilePath));
            int indexSize = int.Parse(srIndex.ReadLine());

            for (int i = 0; i < indexSize; i++)
            {                
                string sentIds = srIndex.ReadLine();
                string[] items = sentIds.Split(' ');

                foreach (var item in items)
                {
                    m_wordId2SentId[i].Add(int.Parse(item));
                }
            }

            srIndex.Close();
        }

        public static void WriteIndex()
        {
            //Write max word id
            //Write vocab (word to id)
            StreamWriter swVocab = new StreamWriter(vocabFilePath);

            swVocab.WriteLine(m_word2id.Count);
            foreach (var item in m_wordId2word)
            {
                //word
                swVocab.WriteLine($"{item}");
            }
            swVocab.Close();

            //Write max sent id
            //Write all sents NoteSent

            StreamWriter swSents = new StreamWriter(sentFilePath);

            swSents.WriteLine(m_sentId2Sent.Count);
            foreach (var item in m_sentId2Sent)
            {
                swSents.WriteLine(item.Id);
                swSents.WriteLine(item.Section);
                swSents.WriteLine(item.Offset);
                swSents.WriteLine(String.Join(" ", item.WordIds));
            }
            swSents.Close();


            //write index (wordid to sentid list)
            StreamWriter swIndex = new StreamWriter(indexFilePath);

            swIndex.WriteLine(m_wordId2SentId.Count);
            foreach (var item in m_wordId2SentId)
            {
                swIndex.WriteLine(String.Join(" ", item));
            }

            swIndex.Close();
        }

        public static object locker = new object();

        public static void BuildIndexUsingTranscript(string filePath)
        {
            string[] turns = File.ReadAllLines(filePath);
            int turnIdx = 0;

            foreach (var turn in turns)
            {
                string[] spans = turn.Split(".\\guessed", StringSplitOptions.RemoveEmptyEntries);
                int spanIdx = 0;
                foreach (var span in spans)
                {
                    IndexSent(span, "Transcript", turnIdx.ToString(), spanIdx.ToString());

                    spanIdx++;
                }

                turnIdx++;
            }           
        }


        public static void LabelTurnsInTranscript(string filePath, string prelabeldFilePath)
        {
            var prelabeledDict = LoadPrelabeledFile(prelabeldFilePath);
            List<string> results = new List<string>();

            string[] turns = File.ReadAllLines(filePath);
            int turnIdx = 0;

            foreach (var turn in turns)
            {
                string[] spans = turn.Split(".\\guessed", StringSplitOptions.RemoveEmptyEntries);
                int spanIdx = 0;
                bool hasPrelabeledSpan = false;
                List<string> prelabeledTurns = new List<string>();

                foreach (var span in spans)
                {
                    string keyword = $"{turnIdx}-{spanIdx}";
                    if (prelabeledDict.ContainsKey(keyword) == true)
                    {
                        hasPrelabeledSpan = true;

                        prelabeledTurns.Add($"{prelabeledDict[keyword]}\t{span}");
                    }
                    else
                    {
                        prelabeledTurns.Add($"Other\t{span}");
                    }

                    spanIdx++;
                }

                if (hasPrelabeledSpan)
                {
                    prelabeledTurns.Add("");
                    results.AddRange(prelabeledTurns);
                }

                turnIdx++;
            }

            File.WriteAllLines("prelabeledCorpus.txt", results);
        }      

      


        public static void IndexSent(string sent, string section, string id, string offset)
        {
            string[] words = sent.Split(' ');

            //Build word index
            List<int> wordIds = new List<int>();
            foreach (var word in words)
            {
                if (m_word2id.ContainsKey(word) == false)
                {
                    m_word2id.TryAdd(word, m_maxWordId);
                    m_wordId2word.Add(word);
                    m_wordId2SentId.Add(new List<int>());

                    m_maxWordId++;
                }

                wordIds.Add(m_word2id[word]);
            }

            //Build Sent Index
            var noteSent = new IndexedSent();
            noteSent.Section = section;
            noteSent.Sent = sent;
            noteSent.WordIds = wordIds;
            noteSent.Id = id;
            noteSent.Offset = offset;

            m_sentId2Sent.Add(noteSent);

            //Build word Id to sent Id index
            HashSet<int> setWordIds = new HashSet<int>();
            foreach (var wordId in wordIds)
            {
                if (setWordIds.Contains(wordId) == true)
                {
                    continue;
                }

                m_wordId2SentId[wordId].Add(m_maxSentId);

                setWordIds.Add(wordId);
            }

            m_maxSentId++;
        }



        public static bool IsTitle(string line)
        {
            line = line.Trim();
            if (line.EndsWith(":") == false)
            {
                return false;
            }

            line = line.Substring(0, line.Length - 1);
            
            foreach (char ch in line)
            {
                if (char.IsUpper(ch) == false && ch != ' ')
                {
                    return false;
                }
            }

            return true;
        }
             
        public static List<string> SplitSent(string note)
        {
            List<string> results = new List<string>();
            string[] lines = note.Split("\\n", StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (IsTitle(line) == true)
                {
                    continue;
                }

                string[] subLines = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                List<string> currLine = new List<string>();

                for (int i = 0; i < subLines.Length; i++)
                {
                    char lastCh = subLines[i][subLines[i].Length - 1];
                    string firstCh = (i == subLines.Length - 1) ? "" : subLines[i + 1][0].ToString();
                    if (((lastCh == '.' | lastCh == '?' || lastCh == '!') && (String.IsNullOrEmpty(firstCh) == true || Char.IsUpper(firstCh[0]) == true)))
                    {
                        currLine.Add(subLines[i].Substring(0, subLines[i].Length - 1).ToLower());
                        currLine.Add(lastCh.ToString());

                        results.Add(String.Join(" ", currLine));
                        currLine = new List<string>();
                    }
                    else if (lastCh == ',')
                    {
                        currLine.Add(subLines[i].Substring(0, subLines[i].Length - 1).ToLower());
                        currLine.Add(lastCh.ToString());
                    }
                    else
                    {
                        currLine.Add(subLines[i].ToLower());
                    }
                }

                if (currLine.Count > 0)
                {
                    results.Add(String.Join(" ", currLine));
                }
            }


            return results;
        }

        public static List<int> QueryCommonSentence(Dictionary<int, int[]> word2SentId)
        {
            //while (word2SentId.Count > 0)
            //{
                int minCount = 9999999;
                int keyWord = -1;
                foreach (KeyValuePair<int, int[]> pair in word2SentId)
                {
                    if (pair.Value.Length < minCount)
                    {
                        keyWord = pair.Key;
                        minCount = pair.Value.Length;
                    }
                }

                if (minCount < 9999999)
                {
                    List<int> resultList = new List<int>(4000);
                    int lastStandIndex = -1;
                    foreach (int standIndex in word2SentId[keyWord])
                    {
                        if (standIndex == lastStandIndex)
                        {
                            continue;
                        }

                        bool flag = true;
                        foreach (KeyValuePair<int, int[]> pair in word2SentId)
                        {
                            if (pair.Key == keyWord)
                            {
                                continue;
                            }

                            if (Array.BinarySearch<int>(pair.Value, standIndex) < 0)
                            {
                                flag = false;
                                break;
                            }
                        }

                        if (flag == true)
                        {
                            resultList.Add(standIndex);
                            if (resultList.Count > 4000)
                            {
                                return resultList;
                            }
                        }

                        lastStandIndex = standIndex;
                    }

                    if (resultList.Count > 0)
                    {
                        return resultList;
                    }
                }

            //    word2SentId = RemoveWordWithLongestSents(word2SentId);
            //}

            return null;
        }

        //static HashSet<string> stopwords = new HashSet<string>() {"the", "i", ".", ",", "?", "!", "a", "you", "she", "he", "him", "her", "and", ":", "his", "your", "my", "in", "on",
        //    "or", "but", "is", "was", "were", "are", "am", "to", "be", "of", "by", "this", "that", "these", "those", "do", "does", "did", "can", "maybe", "have", "has", "had", "having" };


        static HashSet<string> stopwords = new HashSet<string>() {"the", ".", ",", "?", "!", "a", ":" };


        static List<int> ConvertQueryToWordIdList(string query)
        {
            query = query.ToLower();

            string[] words = query.Split(' ');
            List<int> wordIds = new List<int>();

            foreach (var word in words)
            {
                if (stopwords.Contains(word))
                {
                    continue;
                }

                if (m_word2id.ContainsKey(word))
                {
                    wordIds.Add(m_word2id[word]);
                }
            }

            return wordIds;
        }

        static List<int> GetCandidatesSentIdList(List<int> wordIds)
        {
            Dictionary<int, int[]> wordId2SentIds = new Dictionary<int, int[]>();
            foreach (var wordId in wordIds)
            {
                if (wordId2SentIds.ContainsKey(wordId) == false)
                {
                    wordId2SentIds.Add(wordId, m_wordId2SentId[wordId].ToArray());
                }
            }

            List<int> results = QueryCommonSentence(wordId2SentIds);

            return results;
        }


        static List<IndexedSent> GetIndexedSentBySentIdList(List<int> candSentIds)
        {
            List<IndexedSent> results = new List<IndexedSent>();

            foreach (var candSentId in candSentIds)
            {
                results.Add(m_sentId2Sent[candSentId]);
            }

            return results;
        }


        /// <summary>
        /// 求一个字符串的回溯函数。
        /// 约定序列下标从0开始。
        /// 回溯函数是整数集[0,n-1]到N的映射，n为字符串的长度。
        /// 回溯函数的定义：
        /// 设存在非空序列L，i为其合法下标；
        /// L[i]的前前置序列集为：{空集,L中所有以i-1为最后一个元素下标的子序列}
        /// L的前置序列集为：{空集,L中所有以0为第一个元素下标的子序列}
        /// 下标i的回溯函数值的定义为：
        /// 如果i=0,回溯函数值为-1
        /// 否则i的回溯函数值为i的前置序列集和L的前置序列集中相等元素的最大长度
        /// 换句话说是，设集合V={x,x属于i的前置序列集,并且x属于L的前置序列集，并且x的长度小于i}，回溯函数值为max(V)
        /// 当i=0时并不存在这样的一个x，所以约定此时的回溯函数值为-1
        /// 回溯函数的意义：
        /// 如果标号为j的字符同主串失配，那么将子串回溯到next[j]继续与主串匹配，如果next[j]=-1,则主串的匹配点后移一位，同子串的第一个元素开始匹配。
        /// 同一般的模式匹配算法相比，kmp通过回溯函数在失配的情况下跳过了若干轮匹配(向右滑动距离可能大于1)
        /// kmp算法保证跳过去的这些轮匹配一定是失配的，这一点可以证明
        /// </summary>
        /// <param name="pattern">模式串，上面的注释里将其称为子串</param>
        /// <returns>回溯函数是kmp算法的核心，本函数依照其定义求出回溯函数，KMP函数依照其意义使用回溯函数。</returns>
        public static int[] Next(ref int[] pattern)
        {
            int[] next = new int[pattern.Length];
            next[0] = -1;
            if (pattern.Length < 2) //如果只有1个元素不用kmp效率会好一些
            {
                return next;
            }

            next[1] = 0;    //第二个元素的回溯函数值必然是0，可以证明：
            //1的前置序列集为{空集,L[0]}，L[0]的长度不小于1，所以淘汰，空集的长度为0，故回溯函数值为0
            int i = 2;  //正被计算next值的字符的索引
            int j = 0;  //计算next值所需要的中间变量，每一轮迭代初始时j总为next[i-1]
            while (i < pattern.Length)    //很明显当i==pattern.Length时所有字符的next值都已计算完毕，任务已经完成
            { //状态点
                if (pattern[i - 1] == pattern[j])   //首先必须记住在本函数实现中，迭代计算next值是从第三个元素开始的
                {   //如果L[i-1]等于L[j]，那么next[i] = j + 1
                    next[i++] = ++j;
                }
                else
                {   //如果不相等则检查next[i]的下一个可能值----next[j]
                    j = next[j];
                    if (j == -1)    //如果j == -1则表示next[i]的值是1
                    {   //可以把这一部分提取出来与外层判断合并
                        //书上的kmp代码很难理解的一个原因就是已经被优化，从而遮蔽了其实际逻辑
                        next[i++] = ++j;
                    }
                }
            }
            return next;
        }

        /// <summary>
        /// KMP函数同普通的模式匹配函数的差别在于使用了next函数来使模式串一次向右滑动多位称为可能
        /// next函数的本质是提取重复的计算
        /// </summary>
        /// <param name="source">主串</param>
        /// <param name="pattern">用于查找主串中一个位置的模式串</param>
        /// <returns>-1表示没有匹配，否则返回匹配的标号</returns>
        public static int ExecuteKMP(List<int> source, int[] pattern)
        {
            int[] next = Next(ref pattern);
            int i = 0;  //主串指针
            int j = 0;  //模式串指针
            //如果子串没有匹配完毕并且主串没有搜索完成
            while (j < pattern.Length && i < source.Count)
            {
                if (source[i] == pattern[j])    //i和j的逻辑意义体现于此，用于指示本轮迭代中要判断是否相等的主串字符和模式串字符
                {
                    i++;
                    j++;
                }
                else
                {
                    j = next[j];    //依照指示迭代回溯
                    if (j == -1)    //回溯有情况，这是第二种
                    {
                        i++;
                        j++;
                    }
                }
            }
            //如果j==pattern.Length则表示循环的退出是由于子串已经匹配完毕而不是主串用尽
            return j < pattern.Length ? -1 : i - j;
        }


        public static bool IsPunctuation(int termId)
        {
            string str;

            str = m_wordId2word[termId];
            if (str == "。" ||
                str == "，" ||
                str == "," ||
                str == "!" ||
                str == "+" ||
                str == "." ||
                str == "?" ||
                str == "、" ||
                str == ":" ||
                str == ";" ||
                str == "·" ||
                str == "…")
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public static bool CalcInvertedSequence(SortedList<int, QueryTermProp> queryIdList, int queryLength, int[] calcWordList, out int score)
        {
            List<int> IdList = new List<int>();
            bool bFirst = true;
            int totalTermSpace = 0;
            int lastTermIndex = 0;

            int goodSynScore = 0;
            int normalSynScore = 0;
            int badSynScore = 0;

            int tempPunctuationNum = 0;
            int punctuationNum = 0;
            int i = 0;

            int[] termOccupyBitmap;
            termOccupyBitmap = new int[queryLength];
            for (i = 0; i < termOccupyBitmap.Length; i++)
            {
                termOccupyBitmap[i] = 0;
            }

            i = 0;
            foreach (int item in calcWordList)
            {
                if (bFirst == false && IsPunctuation(item) == true)
                {
                    tempPunctuationNum++;
                }

                int id;
                QueryTermProp qtProp;
                if (queryIdList.TryGetValue(item, out qtProp) == true)
                {
                    termOccupyBitmap[qtProp.offset]++;
                    switch (qtProp.synType)
                    {
                        case 0:
                            break;
                        case 1:
                        case 7:
                            goodSynScore++;
                            break;
                        case 2:
                        case 5:
                            normalSynScore++;
                            break;
                        case 6:
                        case 9:
                            badSynScore++;
                            break;
                        default:
                            break;
                    }
                    IdList.Add(qtProp.offset);

                    if (bFirst == false)
                    {
                        totalTermSpace += (i - lastTermIndex - 1);
                        punctuationNum += tempPunctuationNum;
                        tempPunctuationNum = 0;
                    }
                    else
                    {
                        bFirst = false;
                    }
                    lastTermIndex = i;
                }
                i++;
            }

            int r = 0;
            RecurCalcIS(ref IdList, 0, IdList.Count, ref r);

            int finePoint = 0;
            for (i = 0; i < termOccupyBitmap.Length; i++)
            {
                if (termOccupyBitmap[i] == 0)
                {
                    finePoint++;
                }
            }

            score = (r * 100 + totalTermSpace + badSynScore * 10 + normalSynScore * 5 + punctuationNum * 50 + finePoint * 150) * iRankLevelFactor + goodSynScore * 2;

            if (finePoint == 0)
            {
                return true;
            }

            return false;
        }

        public static void RecurCalcIS(ref List<int> IdList, int l, int r, ref int cnt)
        {
            int mid, i, j, tmp;
            int[] c;

            c = new int[IdList.Count];

            if (r > l + 1)
            {
                mid = (l + r) / 2;
                RecurCalcIS(ref IdList, l, mid, ref cnt);
                RecurCalcIS(ref IdList, mid, r, ref cnt);
                tmp = l;

                for (i = l, j = mid; i < mid && j < r;)
                {

                    if (IdList[i] > IdList[j])
                    {
                        c[tmp++] = IdList[j++];
                        cnt += mid - i;
                    }
                    else
                    {
                        c[tmp++] = IdList[i++];
                    }
                }
                if (j < r)
                {
                    for (; j < r; ++j)
                    {
                        c[tmp++] = IdList[j];
                    }
                }
                else
                {
                    for (; i < mid; ++i)
                    {
                        c[tmp++] = IdList[i];
                    }
                }

                for (i = l; i < r; ++i)
                {
                    IdList[i] = c[i];
                }
            }
        }

        public static List<IndexedSent> RankSentencePair(List<IndexedSent> sentpairList, int[] scores, int topN)
        {
            SortedDictionary<int, List<IndexedSent>> sorted = new SortedDictionary<int, List<IndexedSent>>();
            List<IndexedSent> r = new List<IndexedSent>();

            for (int i = 0;i < sentpairList.Count;i++)            
            {
                var item = sentpairList[i];
                var score = scores[i];

                if (sorted.ContainsKey(score) == false)
                {
                    sorted.Add(score, new List<IndexedSent>());
                }

                sorted[score].Add(item);
            }

            HashSet<IndexedSent> savedOffset = new HashSet<IndexedSent>();
            foreach (KeyValuePair<int, List<IndexedSent>> pair in sorted)
            {
                foreach (IndexedSent item in pair.Value)
                {
                    if (savedOffset.Contains(item) == true)
                    {
                        continue;
                    }
                    savedOffset.Add(item);


                    if (topN <= 0)
                    {
                        return r;
                    }

                    r.Add(item);
                    topN--;
                }
            }

            return r;
        }

        public static string ConstructStringFromWordIds(List<int> wordIds)
        {
            List<string> words = new List<string>();

            foreach (int id in wordIds)
            {
                words.Add(m_wordId2word[id]);
            }

            return String.Join(" ", words);
        }


        public static Dictionary<string, Dictionary<string, int>> turnId2Tag2Num = new Dictionary<string, Dictionary<string, int>>();
        public static Dictionary<string, string> turnId2StrSent = new Dictionary<string, string>();
        public static HashSet<string> setQueries = new HashSet<string>();

        public static void BatchQuery(Dictionary<string, string> noteSent2Tag)
        {
            int processedNum = 0;
            int totalQueryNum = 0;
            int resultQueryNum = 0;

            Parallel.ForEach(noteSent2Tag, pair =>
            {
                string filePath = pair.Key;
                string tag = pair.Value;

                Console.WriteLine($"Processing  '{filePath}'...");

                string[] lines = File.ReadAllLines(filePath);
                Parallel.ForEach(lines, line =>
                {
                    Interlocked.Increment(ref processedNum);

                    if (processedNum % 100 == 0)
                    {
                        Console.WriteLine($"Processed Num = '{processedNum}', Total Query Num = '{totalQueryNum}', Result Query Num = '{resultQueryNum}'");
                    }

                    if (processedNum % 60000 == 0)
                    {
                        lock (locker)
                        {
                            List<string> results = OutputTurnIdWithTagNum();
                            File.WriteAllLines($"output.txt", results);
                        }
                    }

                    List<string> queries = SplitSent(line);
                    foreach (var query in queries)
                    {
                        lock (locker)
                        {
                            if (setQueries.Contains(query) == true)
                            {
                                continue;
                            }
                            setQueries.Add(query);
                        }

                        List<IndexedSent> rankedSents = Query(query, 1);

                        Interlocked.Increment(ref totalQueryNum);
                        if (rankedSents != null && rankedSents.Count > 0)
                        {
                            Interlocked.Increment(ref resultQueryNum);
                            var sent = rankedSents[0];
                            string turnId = $"{sent.Id}-{sent.Offset}";

                            lock (locker)
                            {
                                if (turnId2StrSent.ContainsKey(turnId) == false)
                                {
                                    string strSent = ConstructStringFromWordIds(sent.WordIds);
                                    turnId2StrSent.Add(turnId, strSent);
                                }


                                if (turnId2Tag2Num.ContainsKey(turnId) == false)
                                {
                                    turnId2Tag2Num.Add(turnId, new Dictionary<string, int>());
                                }

                                if (turnId2Tag2Num[turnId].ContainsKey(tag) == false)
                                {
                                    turnId2Tag2Num[turnId].Add(tag, 0);
                                }

                                turnId2Tag2Num[turnId][tag]++;
                            }
                        }

                    }
                });
            });
        }

        public static List<string> OutputTurnIdWithTagNum()
        {
            List<string> results = new List<string>();

            SortedDictionary<int, List<string>> freq2results = new SortedDictionary<int, List<string>>();

            foreach (KeyValuePair<string, Dictionary<string, int>> pair in turnId2Tag2Num)
            {
                int freq = 0;
                SortedDictionary<int, List<string>> num2Tags = new SortedDictionary<int, List<string>>();

                foreach (KeyValuePair<string, int> subPair in pair.Value)
                {
                    if (num2Tags.ContainsKey(subPair.Value) == false)
                    {
                        num2Tags.Add(subPair.Value, new List<string>());
                    }
                    num2Tags[subPair.Value].Add(subPair.Key);
                }

                List<string> tagNums = new List<string>();
                foreach (var subpair in num2Tags.Reverse())
                {
                    foreach (var item in subpair.Value)
                    {
                        tagNums.Add($"{item} {subpair.Key}");

                        freq += subpair.Key;
                    }

                }

                string strTagNums = String.Join("\t", tagNums);
                string outputLine = $"{turnId2StrSent[pair.Key]}\t{pair.Key}\t{strTagNums}";

                if (freq2results.ContainsKey(freq) == false)
                {
                    freq2results.Add(freq, new List<string>());
                }
                freq2results[freq].Add(outputLine);

            }

            foreach (var pair in freq2results.Reverse())
            {
                results.AddRange(pair.Value);
            }

            return results;
        }


        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("TSCRelevance [buildIndex] [corpus file path for index building] [true casing model file path]");

                Console.WriteLine("TSCRelevance [query] [root path for index files]");
                Console.WriteLine("TSCRelevance [batchquery] [root path for index files] [file path for queries] ...");

                Console.WriteLine("TSCRelevance [prelabel] [corpus file path for labeling] [true casing model file path] [pre-labeled corpus file path]");
                return;
            }

            string mode = args[0];

            if (mode.Equals("buildIndex"))
            {
                string indexCorpusFilePath = args[1];
                Console.WriteLine($"Building index from '{indexCorpusFilePath}'");
                BuildIndexUsingTranscript(indexCorpusFilePath);


                Console.WriteLine("Write index to files");
                WriteIndex();
            }
            else if (mode.Equals("prelabel"))
            {
                //LabelTurnsInTranscript

                string corpusFilePath = args[1];
                string prelabeldCorpusFilePath = args[3];

                LabelTurnsInTranscript(corpusFilePath, prelabeldCorpusFilePath);

            }
            else if (mode.Equals("batchquery"))
            {
                string rootPath = args[1];
                Console.WriteLine($"Loading index files from {rootPath}");
                LoadIndex(rootPath);

                Dictionary<string, string> filePath2Tag = new Dictionary<string, string>();
                for (int i = 2; i < args.Length; i++)
                {
                    string[] parts = args[i].Split('.');

                    filePath2Tag.Add(args[i], parts[parts.Length - 2]);
                }

                BatchQuery(filePath2Tag);

                List<string> results = OutputTurnIdWithTagNum();

                File.WriteAllLines($"output.txt", results);


            }
            else if (mode.Equals("query"))
            {
                string rootPath = args[1];
                Console.WriteLine($"Loading index files from {rootPath}");
                LoadIndex(rootPath);

                string query = "";
                while (true)
                {
                    Console.Write("Query: ");
                    query = Console.ReadLine();

                    List<IndexedSent> rankedSents = Query(query);
                    if (rankedSents == null)
                    {
                        Console.WriteLine($"No result for '{query}'");
                    }
                    else
                    {
                        foreach (IndexedSent noteSent in rankedSents)
                        {
                            string sent = ConstructStringFromWordIds(noteSent.WordIds);

                            Console.WriteLine($"Section: '{noteSent.Section}'");
                          //  Console.WriteLine($"Score: '{noteSent.score}'");
                            Console.WriteLine($"Id: {noteSent.Id}");
                            Console.WriteLine($"Offset: '{noteSent.Offset}'");
                            Console.WriteLine($"Sent: '{sent}'");
                        }
                    }
                }
            }
            else
            {
                throw new Exception($"Not support mode '{mode}'");
            }       
        }

        private static List<IndexedSent> Query(string query, int numResult = 10)
        {
            List<int> wordIds = ConvertQueryToWordIdList(query);
            if (wordIds.Count <= 1)
            {
                return null;
            }


            List<int> candSentIds = GetCandidatesSentIdList(wordIds);

            if (candSentIds == null || candSentIds.Count == 0)
            {
                return null;
            }

            List<IndexedSent> indexedSents = GetIndexedSentBySentIdList(candSentIds);

            List<IndexedSent> rankedSents = new List<IndexedSent>();
            if (indexedSents != null && indexedSents.Count > 0)
            {
                SortedList<int, QueryTermProp> queryIdList = new SortedList<int, QueryTermProp>();
                int pos = 0;
                foreach (int termId in wordIds)
                {
                    if (queryIdList.ContainsKey(termId) == false)
                    {
                        QueryTermProp prop = new QueryTermProp();
                        prop.offset = pos;
                        prop.synSrcTerm = termId;
                        prop.synType = 0;
                        prop.termId = termId;

                        queryIdList.Add(termId, prop);

                        pos++;
                    }
                }

                int i;
                int[] scores = new int[indexedSents.Count];

                for (i = 0; i < indexedSents.Count; i++)
                {
                    int r = 0;

                    if (wordIds.Count == 0 || ExecuteKMP(wordIds, indexedSents[i].WordIds.ToArray()) == -1)
                    {
                        CalcInvertedSequence(queryIdList, queryIdList.Count, indexedSents[i].WordIds.ToArray(), out r);
                    }
                    r += indexedSents[i].WordIds.Count;

                    scores[i] = r;
                }
                rankedSents = RankSentencePair(indexedSents, scores, numResult);
            }

            return rankedSents;
        }
    }
}
